using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>A virtual hard disk attached to a VM's current configuration or to one of its checkpoints.</summary>
public sealed record DiskAttachment(Guid VmId, string VmName, string Path, bool InCheckpoint);

/// <summary>Every VM's disk attachments and checkpoint counts, read in one pass.</summary>
public sealed record StorageSnapshot(IReadOnlyList<DiskAttachment> Disks, IReadOnlyDictionary<Guid, int> CheckpointCounts)
{
    public int CheckpointsOf(Guid vmId) => CheckpointCounts.GetValueOrDefault(vmId);
}

/// <summary>Hyper-V storage and deletion operations used by lifecycle jobs.</summary>
public interface IHyperVStorage
{
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be reached.</exception>
    Task<StorageSnapshot> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The parent of a differencing disk, or null for a disk without one or a file Hyper-V cannot read.</summary>
    Task<string?> GetParentPathAsync(string path, CancellationToken cancellationToken);

    /// <summary>Deletes every checkpoint of the VM, merging their differencing disks into its disks.</summary>
    /// <param name="progress">Receives 0 to 100 as merging proceeds.</param>
    Task DeleteCheckpointsAsync(Guid vmId, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Removes the VM's configuration from Hyper-V. Its disk files are left in place.</summary>
    Task DeleteVmAsync(Guid vmId, CancellationToken cancellationToken);
}

/// <summary>File operations on virtual hard disk files.</summary>
public interface IDiskFiles
{
    bool Exists(string path);

    /// <summary>True when the host service could delete the file now: it has the right and nothing has it open.</summary>
    bool CanDelete(string path);

    /// <exception cref="IOException">The file could not be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The service lacks the right to delete it.</exception>
    void Delete(string path);

    /// <summary>Virtual hard disk files (.vhd, .vhdx, .avhd, .avhdx) directly in <paramref name="directory"/>.</summary>
    IEnumerable<string> ListDiskFiles(string directory);
}

/// <summary>Path rules for virtual hard disk files.</summary>
public static class DiskPaths
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    private static readonly string[] DiskExtensions = [".vhd", ".vhdx", ".avhd", ".avhdx"];

    /// <summary>A full path without the \\?\ prefix or a trailing separator, for comparisons.</summary>
    public static string Normalize(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        return Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar);
    }

    public static bool IsDisk(string path) =>
        DiskExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Checkpoints write to .avhd and .avhdx files, which are merged away when the checkpoints are deleted.</summary>
    public static bool IsCheckpointDisk(string path) =>
        Path.GetExtension(path) is { } extension
        && (extension.Equals(".avhd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".avhdx", StringComparison.OrdinalIgnoreCase));
}

/// <summary><see cref="IDiskFiles"/> on the local file system.</summary>
public sealed class WindowsDiskFiles : IDiskFiles
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareAll = 0x7;
    private const uint OpenExisting = 3;

    public bool Exists(string path) => File.Exists(path);

    public bool CanDelete(string path)
    {
        // Opening with DELETE access checks both the ACL and whether another process holds the file.
        using var handle = CreateFileW(path, DeleteAccess, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    public void Delete(string path) => File.Delete(path);

    public IEnumerable<string> ListDiskFiles(string directory) =>
        Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory).Where(DiskPaths.IsDisk)
            : [];

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

/// <summary>A lifecycle request that is invalid as sent (400).</summary>
public sealed class LifecycleValidationException : Exception
{
    public LifecycleValidationException(string message, IReadOnlyList<Shared.Contracts.ValidationIssue>? errors = null)
        : base(message)
    {
        Errors = errors ?? [];
    }

    public IReadOnlyList<Shared.Contracts.ValidationIssue> Errors { get; }
}

/// <summary>A lifecycle request that conflicts with the VM's current state (409).</summary>
public sealed class LifecycleConflictException : Exception
{
    public LifecycleConflictException(string message, string? code = null)
        : base(message)
    {
        Code = code;
    }

    /// <summary>The problem details code, when the client handles this conflict specially.</summary>
    public string? Code { get; }
}

/// <summary>The VM was deleted but some of its disk files could not be.</summary>
public sealed class DiskDeletionException : Exception
{
    public DiskDeletionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The request is valid but would leave the host short of resources (409). Sending it again with
/// acknowledgeWarnings set proceeds.
/// </summary>
public sealed class ResourceWarningsException : Exception
{
    public ResourceWarningsException(IReadOnlyList<Shared.Contracts.ValidationIssue> warnings)
        : base("Check these warnings, then send the request again with acknowledgeWarnings to continue.")
    {
        Warnings = warnings;
    }

    public IReadOnlyList<Shared.Contracts.ValidationIssue> Warnings { get; }
}
