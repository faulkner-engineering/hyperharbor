using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Backup;

/// <summary>Schema: BackupBrowseState.</summary>
public enum BackupBrowseState
{
    Preparing,
    Ready,
    Failed,
}

/// <summary>Schema: OpenBackupBrowseRequest.</summary>
public sealed record OpenBackupBrowseRequest([property: JsonRequired] string TargetId, [property: JsonRequired] string SnapshotId);

/// <summary>Schema: BackupBrowseVolume.</summary>
/// <param name="Drive">The drive letter the volume has on the host, for example "E:".</param>
public sealed record BackupBrowseVolume(string Drive, long SizeBytes, string? Label = null);

/// <summary>Schema: BackupBrowseSession.</summary>
/// <param name="Step">What the host is doing while preparing.</param>
/// <param name="Error">Why the session failed.</param>
public sealed record BackupBrowseSession(
    string Id,
    BackupBrowseState State,
    string Step,
    IReadOnlyList<BackupBrowseVolume> Volumes,
    DateTimeOffset ExpiresAt,
    string? VmName = null,
    string? Error = null);

/// <summary>Schema: BackupBrowsePath.</summary>
/// <param name="Path">A path inside the volume; omitted for the volume's root.</param>
public sealed record BackupBrowsePath([property: JsonRequired] string Volume, string? Path = null);

/// <summary>Schema: BackupBrowseEntry.</summary>
public sealed record BackupBrowseEntry(string Name, bool IsDirectory, long Size, DateTimeOffset? Modified = null);
