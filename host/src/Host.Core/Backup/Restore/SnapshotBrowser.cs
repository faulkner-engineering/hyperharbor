using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.HostProfiles;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup.Restore;

/// <summary>A volume of a mounted backup disk that Windows could read, with a drive letter.</summary>
/// <param name="Drive">"E:" for a real mount.</param>
public sealed record BrowseVolume(string Drive, string? Label, long SizeBytes);

public sealed record BrowseEntry(string Name, bool IsDirectory, long Size, DateTimeOffset? Modified);

public enum BrowseState
{
    /// <summary>Restoring the snapshot's files and mounting its disks.</summary>
    Preparing,
    Ready,
    Failed,
}

public sealed record BrowseSessionInfo(
    string Id,
    BrowseState State,
    string Step,
    string? VmName,
    IReadOnlyList<BrowseVolume> Volumes,
    string? Error,
    DateTimeOffset ExpiresAt);

/// <summary>Mounts a restored VHDX read-only on the host so its files can be read.</summary>
public interface IVhdMounter
{
    /// <returns>The volumes of the disk that have a drive letter.</returns>
    Task<IReadOnlyList<BrowseVolume>> MountAsync(string imagePath, CancellationToken cancellationToken);

    /// <summary>Detaches the disk. Does nothing when it is not mounted.</summary>
    Task DismountAsync(string imagePath, CancellationToken cancellationToken);
}

/// <summary><see cref="IVhdMounter"/> through the Storage cmdlets of Windows PowerShell (Mount-DiskImage, read-only).</summary>
public sealed class PowerShellVhdMounter : IVhdMounter
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    private static readonly string Script = HostPowerShell.Encode("""
        $ErrorActionPreference = 'Stop'
        try {
            $r = [Console]::In.ReadToEnd() | ConvertFrom-Json
            if ($r.action -eq 'mount') {
                Mount-DiskImage -ImagePath $r.path -Access ReadOnly -StorageType VHDX | Out-Null
                $volumes = @(Get-DiskImage -ImagePath $r.path | Get-Disk | Get-Partition | Get-Volume | Where-Object { $_.DriveLetter } |
                    ForEach-Object { @{ drive = "$($_.DriveLetter):"; label = $_.FileSystemLabel; size = [long]$_.Size } })
                @{ ok = $true; result = $volumes } | ConvertTo-Json -Compress -Depth 5
            } else {
                try { Dismount-DiskImage -ImagePath $r.path | Out-Null } catch { }
                @{ ok = $true; result = @() } | ConvertTo-Json -Compress
            }
        } catch {
            @{ ok = $false; error = "$($_.Exception.Message)"; stage = 'mount' } | ConvertTo-Json -Compress
        }
        """);

    public async Task<IReadOnlyList<BrowseVolume>> MountAsync(string imagePath, CancellationToken cancellationToken)
    {
        var result = await RunAsync("mount", imagePath, cancellationToken).ConfigureAwait(false);
        return (result as JsonArray ?? [])
            .Select(node => new BrowseVolume((string)node!["drive"]!, (string?)node["label"], (long?)node["size"] ?? 0))
            .ToList();
    }

    public Task DismountAsync(string imagePath, CancellationToken cancellationToken) => RunAsync("dismount", imagePath, cancellationToken);

    private static async Task<JsonNode?> RunAsync(string action, string imagePath, CancellationToken cancellationToken)
    {
        try
        {
            return await HostPowerShell.RunAsync(Script, new { action, path = imagePath }, Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (HostLeanException ex)
        {
            throw new RestoreException($"The disk could not be mounted for browsing: {ex.Message}", ex);
        }
    }
}

/// <summary>Resolves a path typed by a client inside a mounted volume. Pure apart from reading file attributes.</summary>
public static class BrowsePaths
{
    /// <summary>
    /// The full path of <paramref name="relative"/> under <paramref name="volumeRoot"/>. Absolute paths, "..", alternate
    /// data streams, and reserved device names are refused, and so is any component that is a reparse point: a junction in a
    /// guest disk points at a path of the guest, which on the host would mean the host's own files.
    /// </summary>
    /// <exception cref="RestoreException">The path is not allowed.</exception>
    public static string Resolve(string volumeRoot, string? relative)
    {
        var root = Path.TrimEndingDirectorySeparator(volumeRoot) + Path.DirectorySeparatorChar;
        var parts = (relative ?? string.Empty).Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsDeviceName(part))
            {
                throw new RestoreException("That path is not allowed.");
            }

            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new RestoreException("That path is a link, which cannot be followed in a backup.");
                }
            }
        }

        var full = Path.GetFullPath(current);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new RestoreException("That path is not allowed.");
        }

        return full;
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].TrimEnd().ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL"
            || (name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(name[3]));
    }
}

/// <summary>
/// Looks inside a backup without restoring a VM: restores the snapshot's files into a work folder, mounts the disks
/// read-only, and lets a client list folders and download files. One session at a time. It ends when closed, after
/// <see cref="IdleLimit"/> without use, or at host start. The mounted drives are visible to Windows while it is open.
/// </summary>
public sealed class SnapshotBrowser(
    IBackupRepositories repositories,
    ISnapshotRestorer restorer,
    IVhdMounter mounter,
    Func<CancellationToken, Task<string>> workRoot,
    TimeProvider time,
    ILogger<SnapshotBrowser> logger)
{
    public const string FolderPrefix = ".hh-browse-";
    public const int MaxEntries = 5000;
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private Session? _session;

    /// <exception cref="BackupException">A snapshot is already open.</exception>
    public BrowseSessionInfo Open(BackupTarget target, string snapshotId)
    {
        Session session;
        lock (_gate)
        {
            if (_session is not null)
            {
                throw new BackupException("A backup is already open for browsing. Close it first.");
            }

            session = _session = new Session(RandomNumberGenerator.GetHexString(32, lowercase: true), time.GetUtcNow() + IdleLimit);
        }

        _ = Task.Run(() => PrepareAsync(session, target, snapshotId));
        return Info(session);
    }

    public BrowseSessionInfo? Get(string id) => Find(id) is { } session ? Info(session) : null;

    /// <exception cref="RestoreException">The session is unknown, not ready, or the path is not allowed.</exception>
    public IReadOnlyList<BrowseEntry> List(string id, string volume, string? path)
    {
        var session = Ready(id);
        var folder = BrowsePaths.Resolve(RootOf(session, volume), path);
        if (!Directory.Exists(folder))
        {
            throw new RestoreException("That folder does not exist in the backup.");
        }

        var entries = new List<BrowseEntry>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
            entries.Add(new BrowseEntry(info.Name, isDirectory, isDirectory ? 0 : ((FileInfo)info).Length, info.LastWriteTimeUtc));
            if (entries.Count >= MaxEntries)
            {
                break;
            }
        }

        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Opens a file for reading. The caller disposes the stream.</summary>
    /// <exception cref="RestoreException">The session is unknown or not ready, the path is not allowed, or it is not a file.</exception>
    public FileStream OpenFile(string id, string volume, string path)
    {
        var session = Ready(id);
        var file = BrowsePaths.Resolve(RootOf(session, volume), path);
        if (!File.Exists(file))
        {
            throw new RestoreException("That file does not exist in the backup.");
        }

        return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public async Task CloseAsync(string id)
    {
        Session? session;
        lock (_gate)
        {
            if (_session is null || _session.Id != id)
            {
                return;
            }

            session = _session;
            _session = null;
        }

        await TearDownAsync(session).ConfigureAwait(false);
    }

    /// <summary>Closes the session that has not been used for <see cref="IdleLimit"/>.</summary>
    public async Task CloseExpiredAsync()
    {
        string? expired;
        lock (_gate)
        {
            expired = _session is { } s && s.ExpiresAt <= time.GetUtcNow() ? s.Id : null;
        }

        if (expired is not null)
        {
            await CloseAsync(expired).ConfigureAwait(false);
        }
    }

    /// <summary>At host start: detaches and deletes what an earlier run left behind.</summary>
    public async Task CleanUpLeftoversAsync(CancellationToken cancellationToken)
    {
        string root;
        try
        {
            root = await workRoot(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "The browse work folder could not be determined.");
            return;
        }

        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var folder in Directory.EnumerateDirectories(root, FolderPrefix + "*"))
        {
            foreach (var image in Directory.EnumerateFiles(folder, "*.vhdx", SearchOption.AllDirectories))
            {
                await TryDismountAsync(image).ConfigureAwait(false);
            }

            TryDelete(folder);
        }
    }

    private async Task PrepareAsync(Session session, BackupTarget target, string snapshotId)
    {
        try
        {
            session.Step = "Opening the backup repository";
            var repository = await repositories.OpenAsync(target, RepositoryRole.Vm, session.Stopping.Token).ConfigureAwait(false);
            var root = await workRoot(session.Stopping.Token).ConfigureAwait(false);
            session.Folder = Path.Combine(root, FolderPrefix + session.Id[..12]);
            Directory.CreateDirectory(session.Folder);

            session.Step = "Restoring the snapshot's files. This can take a while for a large disk.";
            await restorer.RestoreAsync(repository, snapshotId, session.Folder, session.Stopping.Token).ConfigureAwait(false);

            var manifestPath = Path.Combine(session.Folder, BackupManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new RestoreException("The snapshot has no manifest.json, so it is not a HyperHarbor VM backup.");
            }

            BackupManifest manifest;
            try
            {
                manifest = BackupManifest.Parse(await File.ReadAllBytesAsync(manifestPath, session.Stopping.Token).ConfigureAwait(false));
            }
            catch (InvalidDataException ex)
            {
                throw new RestoreException(ex.Message, ex);
            }

            session.VmName = manifest.Vm.Name;
            session.Step = "Mounting the disks read-only";
            var volumes = new List<BrowseVolume>();
            foreach (var disk in manifest.Disks)
            {
                var image = Path.Combine(session.Folder, disk.File);
                if (!File.Exists(image))
                {
                    throw new RestoreException($"The backup is missing the disk {disk.File}.");
                }

                session.Images.Add(image);
                volumes.AddRange(await mounter.MountAsync(image, session.Stopping.Token).ConfigureAwait(false));
            }

            if (volumes.Count == 0)
            {
                throw new RestoreException("No volume of the disk could be read. It may use a file system Windows does not understand, or be encrypted.");
            }

            session.Volumes = volumes;
            session.Step = "Ready";
            session.State = BrowseState.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Opening a backup for browsing failed.");
            session.Error = ex is RestoreException or BackupException or KopiaException ? ex.Message : "The backup could not be opened. The host log has the details.";
            session.State = BrowseState.Failed;
            await TearDownFilesAsync(session).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session.State = BrowseState.Failed;
            session.Error = "Browsing was closed.";
            await TearDownFilesAsync(session).ConfigureAwait(false);
        }
    }

    private async Task TearDownAsync(Session session)
    {
        await session.Stopping.CancelAsync().ConfigureAwait(false);
        await TearDownFilesAsync(session).ConfigureAwait(false);
    }

    private async Task TearDownFilesAsync(Session session)
    {
        foreach (var image in session.Images.ToList())
        {
            await TryDismountAsync(image).ConfigureAwait(false);
        }

        session.Images.Clear();
        TryDelete(session.Folder);
        session.Folder = null;
    }

    private async Task TryDismountAsync(string image)
    {
        try
        {
            await mounter.DismountAsync(image, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The browse disk {Image} could not be detached.", image);
        }
    }

    private void TryDelete(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {Folder} after browsing.", folder);
        }
    }

    private Session? Find(string id)
    {
        lock (_gate)
        {
            return _session is { } s && s.Id == id ? s : null;
        }
    }

    private Session Ready(string id)
    {
        var session = Find(id) ?? throw new RestoreException("That backup is no longer open for browsing.");
        if (session.State != BrowseState.Ready)
        {
            throw new RestoreException("The backup is not ready for browsing yet.");
        }

        session.ExpiresAt = time.GetUtcNow() + IdleLimit;
        return session;
    }

    private static string RootOf(Session session, string volume) =>
        session.Volumes.FirstOrDefault(v => string.Equals(v.Drive, volume, StringComparison.OrdinalIgnoreCase)) is { } found
            ? found.Drive
            : throw new RestoreException("That volume is not part of the backup.");

    private static BrowseSessionInfo Info(Session s) =>
        new(s.Id, s.State, s.Step, s.VmName, s.Volumes, s.Error, s.ExpiresAt);

    private sealed class Session(string id, DateTimeOffset expiresAt)
    {
        public string Id { get; } = id;

        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;

        public volatile BrowseState State;

        public string Step { get; set; } = "Starting";

        public string? VmName { get; set; }

        public string? Error { get; set; }

        public string? Folder { get; set; }

        public IReadOnlyList<BrowseVolume> Volumes { get; set; } = [];

        public List<string> Images { get; } = [];

        public CancellationTokenSource Stopping { get; } = new();
    }
}
