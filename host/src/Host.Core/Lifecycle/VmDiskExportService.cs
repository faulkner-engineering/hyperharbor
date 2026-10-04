using System.Globalization;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Copies files for a disk export, reporting the bytes copied so far.</summary>
public interface IDiskCopier
{
    long Length(string path);

    /// <exception cref="IOException">The copy failed, for example because the destination volume is full.</exception>
    Task CopyAsync(string source, string destination, Action<long> copied, CancellationToken cancellationToken);
}

/// <summary>Plain buffered copies. The destination must not exist yet.</summary>
public sealed class FileDiskCopier : IDiskCopier
{
    private const int BufferBytes = 4 * 1024 * 1024;

    public long Length(string path) => new FileInfo(path).Length;

    public async Task CopyAsync(string source, string destination, Action<long> copied, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous);
        output.SetLength(input.Length);
        var buffer = new byte[BufferBytes];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            copied(total);
        }
    }
}

/// <summary>Where disk exports go when a request names no folder: the tray's choice, else "HyperHarbor Backups" in Public Documents.</summary>
public sealed class BackupLocation(HostSettingsStore settings)
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "HyperHarbor Backups");

    public string Folder => settings.BackupFolder is { Length: > 0 } chosen ? chosen : DefaultFolder;
}

/// <summary>
/// Copies an off VM's virtual hard disks to a new timestamped folder, as a backup before risky changes
/// such as Performance mode. ISO images are never copied. A disk with checkpoints is copied with its
/// whole chain (the .avhdx files and the base disk); the copies keep pointing at the original parents,
/// so restoring one means reconnecting or merging them in Hyper-V Manager.
/// </summary>
public sealed class VmDiskExportService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVStorage _storage;
    private readonly IDiskFiles _files;
    private readonly IDiskCopier _copier;
    private readonly BackupLocation _backups;
    private readonly VmJobStore _jobs;
    private readonly TimeProvider _time;
    private readonly ILogger<VmDiskExportService> _logger;

    public VmDiskExportService(
        IVmInventory inventory,
        IHyperVStorage storage,
        IDiskFiles files,
        IDiskCopier copier,
        BackupLocation backups,
        VmJobStore jobs,
        TimeProvider time,
        ILogger<VmDiskExportService> logger)
    {
        _inventory = inventory;
        _storage = storage;
        _files = files;
        _copier = copier;
        _backups = backups;
        _jobs = jobs;
        _time = time;
        _logger = logger;
    }

    /// <summary>Checks the VM and the destination, then starts a job that copies the disks.</summary>
    /// <param name="destinationFolder">A fully qualified folder, or null for the backup folder.</param>
    /// <exception cref="VmNotFoundException">There is no such VM.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not off (code vmMustBeOff), has no disks, or the destination lacks space.</exception>
    /// <exception cref="LifecycleValidationException">The destination is not a fully qualified path.</exception>
    public async Task<VmJobSnapshot> StartAsync(Guid vmId, Guid userId, string? destinationFolder, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        if (vm.State != VmState.Off)
        {
            throw new LifecycleConflictException(
                $"{vm.Name} must be off to export its disks; it is {vm.State.ToString().ToLowerInvariant()}. Shut it down first.",
                ContractInfo.ProblemCodes.VmMustBeOff);
        }

        var root = destinationFolder?.Trim() is { Length: > 0 } requested ? requested : _backups.Folder;
        if (!Path.IsPathFullyQualified(root))
        {
            throw new LifecycleValidationException("The destination folder is not valid.", [new ValidationIssue("destinationFolder", "Enter a full path, for example D:\\Backups.")]);
        }

        var disks = await ChainAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (disks.Count == 0)
        {
            throw new LifecycleConflictException($"{vm.Name} has no virtual hard disks to export.");
        }

        var totalBytes = disks.Sum(_copier.Length);
        if (_files.AvailableSpaceMb(root) is { } freeMb && freeMb * 1024 * 1024 < totalBytes)
        {
            throw new LifecycleConflictException(string.Create(
                CultureInfo.InvariantCulture,
                $"The export needs {totalBytes / (1024 * 1024 * 1024.0):0.#} GB, but {Path.GetPathRoot(root)} has {freeMb / 1024.0:0.#} GB free."));
        }

        var folder = NewFolder(root, vm.Name);
        return _jobs.Start(VmJobKind.ExportDisks, vmId, userId, "Copying the disks",
            context => CopyAsync(vm.Name, disks, totalBytes, folder, context), onFinished);
    }

    /// <summary>The VM's current disks and their parents, base disks first, without duplicates.</summary>
    private async Task<List<string>> ChainAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var snapshot = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        var chain = new List<string>();
        var seen = new HashSet<string>(DiskPaths.Comparer);
        foreach (var disk in snapshot.Disks.Where(disk => disk.VmId == vmId && !disk.InCheckpoint && DiskPaths.IsDisk(disk.Path)))
        {
            var links = new List<string>();
            for (string? path = DiskPaths.Normalize(disk.Path); path is not null && seen.Add(path); path = await ParentAsync(path, cancellationToken).ConfigureAwait(false))
            {
                links.Add(path);
            }

            links.Reverse();
            chain.AddRange(links);
        }

        return chain;
    }

    private async Task<string?> ParentAsync(string path, CancellationToken cancellationToken) =>
        await _storage.GetParentPathAsync(path, cancellationToken).ConfigureAwait(false) is { Length: > 0 } parent ? DiskPaths.Normalize(parent) : null;

    /// <summary>&lt;root&gt;\&lt;vm&gt;-yyyyMMdd-HHmm, with a number added if that exists.</summary>
    private string NewFolder(string root, string vmName)
    {
        var safe = string.Concat(vmName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        var folder = Path.Combine(root, $"{(safe.Length > 0 ? safe : "VM")}-{stamp}");
        for (var n = 2; Directory.Exists(folder) || File.Exists(folder); n++)
        {
            folder = Path.Combine(root, $"{safe}-{stamp}-{n.ToString(CultureInfo.InvariantCulture)}");
        }

        return folder;
    }

    private async Task CopyAsync(string vmName, IReadOnlyList<string> disks, long totalBytes, string folder, VmJobContext context)
    {
        Directory.CreateDirectory(folder);
        try
        {
            long done = 0;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (disk, index) in disks.Select((disk, index) => (disk, index)))
            {
                var name = Path.GetFileName(disk);
                if (!names.Add(name))
                {
                    name = $"{Path.GetFileNameWithoutExtension(disk)}-{index.ToString(CultureInfo.InvariantCulture)}{Path.GetExtension(disk)}";
                    names.Add(name);
                }

                var step = $"Copying {name} ({index + 1} of {disks.Count})";
                var before = done;
                context.Report(step, Percent(done, totalBytes));
                await _copier.CopyAsync(disk, Path.Combine(folder, name), copied => context.Report(step, Percent(before + copied, totalBytes)), context.Stopping)
                    .ConfigureAwait(false);
                done = before + _copier.Length(disk);
            }

            _logger.LogInformation("Exported {Count} disk files of {Vm} to {Folder}.", disks.Count, vmName, folder);
            context.Report($"Exported to {folder}", 99);
        }
        catch
        {
            TryDelete(folder);
            throw;
        }
    }

    private static int Percent(long done, long total) => total <= 0 ? 0 : (int)Math.Min(99, done * 100 / total);

    private void TryDelete(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not remove the incomplete export {Folder}.", folder);
        }
    }
}
