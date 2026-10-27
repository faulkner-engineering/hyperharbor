using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// Lists a VM's virtual hard disks and grows one, as a growDisk job: the disk file first (while the VM runs for a
/// VHDX on a Generation 2 VM, otherwise with the VM off, shutting it down and starting it again when asked), then the
/// guest's partition at the end of the disk (<see cref="IPartitionExtender"/>). A disk only ever grows.
/// </summary>
public sealed class VmDiskService
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    /// <summary>What a Linux guest needs after its disk grows. Ubuntu installs on LVM by default.</summary>
    internal const string LinuxNote =
        "In the VM, extend the last partition and its file system: sudo growpart /dev/sda 3 (use the partition's number), "
        + "then with LVM (Ubuntu's default) sudo pvresize /dev/sda3 and sudo lvextend -r -l +100%FREE /dev/ubuntu-vg/ubuntu-lv, "
        + "or without LVM sudo resize2fs /dev/sda3.";

    private readonly IVmInventory _inventory;
    private readonly IHyperVStorage _storage;
    private readonly IHyperVDisks _disks;
    private readonly IDiskFiles _files;
    private readonly IPartitionExtender _extender;
    private readonly VmCredentialStore _credentials;
    private readonly IHyperVPowerInvoker _power;
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VmDiskService> _logger;

    public VmDiskService(
        IVmInventory inventory,
        IHyperVStorage storage,
        IHyperVDisks disks,
        IDiskFiles files,
        IPartitionExtender extender,
        VmCredentialStore credentials,
        IHyperVPowerInvoker power,
        VmJobStore jobs,
        LifecycleOptions options,
        TimeProvider time,
        ILogger<VmDiskService> logger)
    {
        _inventory = inventory;
        _storage = storage;
        _disks = disks;
        _files = files;
        _extender = extender;
        _credentials = credentials;
        _power = power;
        _jobs = jobs;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<VmDiskList> ListAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        return new VmDiskList(vmId, await ReadDisksAsync(vm, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Starts a growDisk job.</summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleValidationException">The path is not one of the VM's disks, or the size is not larger.</exception>
    /// <exception cref="ResourceWarningsException">The disk's drive lacks room, and warnings were not acknowledged.</exception>
    /// <exception cref="LifecycleConflictException">The disk cannot grow, or the VM must be off (code requiresShutdown).</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task<VmJobSnapshot> GrowAsync(Guid vmId, Guid userId, GrowVmDiskRequest request, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var disks = await ReadDisksAsync(vm, cancellationToken).ConfigureAwait(false);
        var wanted = DiskPaths.Normalize(request.Path ?? "");
        var disk = disks.FirstOrDefault(candidate => DiskPaths.Comparer.Equals(DiskPaths.Normalize(candidate.Path), wanted))
            ?? throw new LifecycleValidationException($"{request.Path} is not a disk of {vm.Name}.", [new("path", "Use a path from the VM's disk list.")]);

        if (disk.GrowBlocker is { } blocker)
        {
            throw new LifecycleConflictException(Describe(blocker, vm));
        }

        var sizeBytes = (long)request.SizeGb * BytesPerGb;
        if (request.SizeGb is < VmCreationService.MinDiskSizeGb or > VmCreationService.MaxDiskSizeGb)
        {
            throw new LifecycleValidationException("The size is out of range.", [new("sizeGb", $"Use between {VmCreationService.MinDiskSizeGb} and {VmCreationService.MaxDiskSizeGb} GB.")]);
        }

        if (sizeBytes <= disk.MaximumSizeBytes)
        {
            throw new LifecycleValidationException("A disk can only grow.", [new("sizeGb", $"Use more than the disk's {Gb(disk.MaximumSizeBytes)}; a disk cannot shrink.")]);
        }

        // A dynamic disk can fill up to its new maximum, so the drive needs room for what it does not take yet.
        var neededMb = (sizeBytes - disk.FileSizeBytes) / (1024 * 1024);
        if (_files.AvailableSpaceMb(disk.Path) is { } freeMb && neededMb > freeMb && !request.AcknowledgeWarnings)
        {
            throw new ResourceWarningsException([new("sizeGb", $"The disk can grow to {request.SizeGb} GB, but its drive has {VmSettingsValidator.Mb(freeMb)} free.")]);
        }

        var shutDown = false;
        switch (vm.State)
        {
            case VmState.Off:
            case VmState.Running when disk.GrowWhileRunning:
                break;
            case VmState.Running when request.ShutDownToApply:
                shutDown = true;
                break;
            case VmState.Running:
                throw new LifecycleConflictException(
                    $"{vm.Name} must be off to grow this disk, because only a VHDX on a Generation 2 VM grows while the VM runs. Shut it down first, or choose to shut down, grow, and restart.",
                    ContractInfo.ProblemCodes.RequiresShutdown);
            default:
                throw new LifecycleConflictException(Describe(DiskGrowBlocker.VmState, vm));
        }

        return _jobs.Start(VmJobKind.GrowDisk, vmId, userId, shutDown ? "Shutting down the guest" : "Growing the disk",
            context => GrowAsync(vm, disk.Path, sizeBytes, request.ExtendPartition, shutDown, context), onFinished);
    }

    private async Task GrowAsync(Vm vm, string path, long sizeBytes, bool extendPartition, bool shutDown, VmJobContext context)
    {
        if (shutDown)
        {
            await VmShutdown.ShutDownAsync(_power, _inventory, _time, _options.ShutdownTimeoutSeconds, vm.Id,
                $"{vm.Name} did not shut down within {_options.ShutdownTimeoutSeconds} seconds, so the disk was not grown. It was not forced off.",
                context, percentAtOff: 30).ConfigureAwait(false);
        }

        var running = !shutDown && vm.State == VmState.Running;
        context.Report("Growing the disk", shutDown ? 30 : 0);
        await _disks.ResizeAsync(path, sizeBytes, percent => context.Report("Growing the disk", (shutDown ? 30 : 0) + percent * 30 / 100), context.Stopping)
            .ConfigureAwait(false);
        _logger.LogInformation("Grew {Path} of {Name} ({VmId}) to {Size}.", path, vm.Name, vm.Id, Gb(sizeBytes));

        var extension = new PartitionExtension(false, null);
        if (extendPartition)
        {
            context.Report("Extending the partition", 70);
            extension = await ExtendAsync(vm, path, sizeBytes, running, context.Stopping).ConfigureAwait(false);
        }

        if (shutDown)
        {
            context.Report("Starting the virtual machine", 90);
            await _power.InvokeAsync(vm.Id, VmAction.Start, context.Stopping).ConfigureAwait(false);
        }

        context.ReportDiskGrowResult(new DiskGrowResult(path, sizeBytes, extension.Extended, extension.Note));
    }

    private async Task<PartitionExtension> ExtendAsync(Vm vm, string path, long sizeBytes, bool running, CancellationToken cancellationToken)
    {
        var family = vm.GuestOs?.Family ?? GuestOsFamily.Unknown;
        if (family == GuestOsFamily.Linux)
        {
            return new PartitionExtension(false, LinuxNote);
        }

        if (!running)
        {
            var offline = await _extender.ExtendOfflineAsync(path, cancellationToken).ConfigureAwait(false);
            return offline.Extended || family == GuestOsFamily.Windows ? offline : offline with { Note = $"{offline.Note} If the VM runs Linux: {LinuxNote}" };
        }

        if (_credentials.Find(vm.Id) is not { } admin)
        {
            return new PartitionExtension(false, "HyperHarbor has no administrator credential for this VM, so it could not extend the partition. Extend it in Disk Management in the VM.");
        }

        try
        {
            return await _extender.ExtendInGuestAsync(vm.Id, admin, sizeBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GuestUnavailableException or GuestCredentialRejectedException)
        {
            return new PartitionExtension(false, $"The partition could not be extended: {GuestErrors.Clean(ex.Message, admin.Password)} Extend it in Disk Management in the VM.");
        }
    }

    private async Task<IReadOnlyList<VmDisk>> ReadDisksAsync(Vm vm, CancellationToken cancellationToken)
    {
        var storage = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = storage.CheckpointsOf(vm.Id);
        var disks = new List<VmDisk>();
        foreach (var attachment in storage.Disks.Where(disk => disk.VmId == vm.Id && !disk.InCheckpoint))
        {
            var info = await _disks.ReadAsync(attachment.Path, cancellationToken).ConfigureAwait(false);
            DiskGrowBlocker? blocker = info is null ? DiskGrowBlocker.NotFound
                : vm.State is not (VmState.Off or VmState.Running) ? DiskGrowBlocker.VmState
                : checkpoints > 0 ? DiskGrowBlocker.Checkpoints
                : info.ParentPath is not null ? DiskGrowBlocker.Differencing
                : null;
            disks.Add(new VmDisk(
                attachment.Path,
                info?.MaximumSizeBytes ?? 0,
                _files.SizeBytes(attachment.Path) ?? 0,
                info is { IsVhdx: true } && vm.Generation == 2,
                blocker));
        }

        return disks;
    }

    private static string Describe(DiskGrowBlocker blocker, Vm vm) => blocker switch
    {
        DiskGrowBlocker.Checkpoints => $"{vm.Name} has checkpoints, and Hyper-V cannot grow a disk that has them. Merge its checkpoints first.",
        DiskGrowBlocker.Differencing => "This is a differencing disk; only the disk it is based on can grow.",
        DiskGrowBlocker.NotFound => "Hyper-V cannot read this disk file.",
        _ => $"A disk can grow while {vm.Name} is off or running; it is {vm.State.ToString().ToLowerInvariant()}.",
    };

    private static string Gb(long bytes) => $"{bytes / (double)BytesPerGb:0.#} GB";
}
