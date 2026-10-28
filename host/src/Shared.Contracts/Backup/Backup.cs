using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Shared.Contracts.Backup;

/// <summary>Where backups are kept. Schema: BackupKind.</summary>
public enum BackupKind
{
    Local,
    S3,
    Azure,
}

/// <summary>
/// Whether the user saved the target's recovery key. A target with a pending key has no repository and cannot run.
/// Schema: BackupKeyState.
/// </summary>
public enum BackupKeyState
{
    PendingKey,
    Confirmed,
}

/// <summary>Schema: BackupRunOutcome.</summary>
public enum BackupRunOutcome
{
    Succeeded,
    Failed,
    Skipped,

    /// <summary>The backup is stored, but cleanup left something behind.</summary>
    Partial,
}

/// <summary>Schema: BackupRunTrigger.</summary>
public enum BackupRunTrigger
{
    Schedule,
    Shutdown,
    Manual,
}

/// <summary>What to do with a running VM that has a GPU partition. Schema: BackupGpuMode.</summary>
public enum BackupGpuMode
{
    /// <summary>Skip it while it runs; back it up only when off.</summary>
    RequireOff,

    /// <summary>Copy the running VM's disk (crash consistent) and record a warning.</summary>
    AllowLive,
}

/// <summary>How a restored VM ended up. Schema: RestoreVmOutcome.</summary>
public enum RestoreVmOutcome
{
    Succeeded,
    SucceededWithWarnings,

    /// <summary>The VM was restored and started, but the guest never reported in. Use the console.</summary>
    NeedsConsole,
}

/// <summary>A local time of day (HH:mm) on the listed days; all days when <paramref name="Days"/> is omitted. Schema: BackupSchedule.</summary>
public sealed record BackupSchedule([property: JsonRequired] string Time, IReadOnlyList<DayOfWeek>? Days = null);

/// <summary>Schema: BackupRetention.</summary>
public sealed record BackupRetention(int Daily = 7, int Weekly = 4, int Monthly = 6);

/// <summary>Both times are HH:mm, local. The end is exclusive, and a window may cross midnight. Schema: BackupTimeWindow.</summary>
public sealed record BackupTimeWindow([property: JsonRequired] string Start, [property: JsonRequired] string End);

/// <summary>Schema: BackupConditions.</summary>
public sealed record BackupConditions(bool NoActiveSession = true, bool NotInGame = true, BackupTimeWindow? Window = null);

/// <summary>Schema: BackupTriggers.</summary>
public sealed record BackupTriggers(bool Schedule = true, bool OnShutdown = false, int DebounceMinutes = 10);

/// <summary>Schema: BackupLocalSettings.</summary>
public sealed record BackupLocalSettings([property: JsonRequired] string Path);

/// <summary>Schema: BackupS3Settings. The keys are sent in <see cref="BackupCredentials"/> and never returned.</summary>
public sealed record BackupS3Settings(
    [property: JsonRequired] string Endpoint,
    [property: JsonRequired] string Bucket,
    string? Region = null,
    string? Prefix = null,
    bool UseTls = true);

/// <summary>Schema: BackupAzureSettings.</summary>
public sealed record BackupAzureSettings(
    [property: JsonRequired] string Account,
    [property: JsonRequired] string Container,
    string? Prefix = null);

/// <summary>Keys for a remote target. They go into the host's DPAPI vault. Schema: BackupCredentials.</summary>
public sealed record BackupCredentials(string? S3AccessKey = null, string? S3SecretKey = null, string? AzureKey = null)
{
    public override string ToString() => "BackupCredentials { ***** }";
}

/// <summary>Schema: NewBackupTarget.</summary>
public sealed record NewBackupTarget(
    [property: JsonRequired] string Name,
    [property: JsonRequired] BackupKind Kind,
    BackupLocalSettings? Local = null,
    BackupS3Settings? S3 = null,
    BackupAzureSettings? Azure = null,
    BackupCredentials? Credentials = null,
    BackupSchedule? Schedule = null,
    BackupRetention? Retention = null,
    int? BandwidthKbps = null,
    BackupConditions? Conditions = null);

/// <summary>Schema: AttachBackupTargetRequest.</summary>
/// <param name="RecoveryKey">The key saved when the target was first set up.</param>
public sealed record AttachBackupTargetRequest([property: JsonRequired] NewBackupTarget Target, [property: JsonRequired] string RecoveryKey)
{
    public override string ToString() => $"AttachBackupTargetRequest {{ Target = {Target.Name} }}";
}

/// <summary>Changes to a target. Omitted properties stay as they are. Schema: UpdateBackupTargetRequest.</summary>
/// <param name="ClearBandwidth">Removes the bandwidth limit.</param>
public sealed record UpdateBackupTargetRequest(
    string? Name = null,
    bool? Enabled = null,
    BackupSchedule? Schedule = null,
    BackupRetention? Retention = null,
    int? BandwidthKbps = null,
    bool ClearBandwidth = false,
    BackupConditions? Conditions = null,
    BackupCredentials? Credentials = null);

/// <summary>Schema: BackupTarget.</summary>
public sealed record BackupTarget(
    string Id,
    string Name,
    BackupKind Kind,
    bool Enabled,
    BackupKeyState KeyState,
    BackupSchedule Schedule,
    BackupRetention Retention,
    BackupConditions Conditions,
    BackupLocalSettings? Local = null,
    BackupS3Settings? S3 = null,
    BackupAzureSettings? Azure = null,
    int? BandwidthKbps = null);

/// <summary>Shown once. Nothing is stored until the user confirms they saved it. Schema: RecoveryKeyOffer.</summary>
public sealed record RecoveryKeyOffer(string SetupId, string RecoveryKey, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RecoveryKeyOffer {{ SetupId = {SetupId}, RecoveryKey = ***** }}";
}

/// <summary>Schema: ConfirmRecoveryKeyRequest.</summary>
/// <param name="TypedBack">The last four groups of the key, as the user typed them from their saved copy.</param>
public sealed record ConfirmRecoveryKeyRequest([property: JsonRequired] string SetupId, [property: JsonRequired] string TypedBack)
{
    public override string ToString() => $"ConfirmRecoveryKeyRequest {{ SetupId = {SetupId} }}";
}

/// <summary>Schema: BackupTestStep.</summary>
public sealed record BackupTestStep(string Name, bool Passed, string Detail);

/// <summary>Schema: BackupTargetTest.</summary>
public sealed record BackupTargetTest(bool Passed, IReadOnlyList<BackupTestStep> Steps);

/// <summary>A VM's backup settings for one target. The same shape is sent and returned. Schema: BackupPolicy.</summary>
public sealed record BackupPolicy(
    [property: JsonRequired] Guid VmId,
    [property: JsonRequired] string TargetId,
    bool Enabled = true,
    BackupRetention? Retention = null,
    int? BandwidthKbps = null,
    BackupConditions? Conditions = null,
    BackupTriggers? Triggers = null,
    BackupGpuMode GpuMode = BackupGpuMode.RequireOff);

/// <summary>One finished run from the history. Schema: BackupRun.</summary>
/// <param name="Item">"vm:&lt;id&gt;" or "host".</param>
/// <param name="Reason">Why a run failed or was skipped.</param>
/// <param name="UploadedBytes">What the run added to the repository after dedup and compression; null when not measured.</param>
public sealed record BackupRun(
    Guid RunId,
    string Item,
    string TargetId,
    BackupRunTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    BackupRunOutcome Outcome,
    long TotalBytes,
    IReadOnlyList<string> Warnings,
    Guid? VmId = null,
    string? VmName = null,
    string? Reason = null,
    string? SnapshotId = null,
    long? UploadedBytes = null);

/// <summary>How the last run of one item to one target went. Schema: BackupItemStatus.</summary>
/// <param name="Overdue">The last success is older than twice the schedule's longest gap.</param>
public sealed record BackupItemStatus(
    string Item,
    string TargetId,
    string TargetName,
    bool Overdue,
    Guid? VmId = null,
    string? VmName = null,
    BackupRun? LastRun = null,
    DateTimeOffset? LastSuccessAt = null);

/// <summary>Schema: BackupStatus.</summary>
/// <param name="Running">A backup is running now.</param>
/// <param name="Waiting">Runs waiting for their conditions.</param>
public sealed record BackupStatus(IReadOnlyList<BackupItemStatus> Items, bool Running, int Waiting);

/// <summary>Schema: StartBackupRunRequest.</summary>
/// <param name="VmId">The VM to back up; omitted for the host's own state.</param>
public sealed record StartBackupRunRequest([property: JsonRequired] string TargetId, Guid? VmId = null);

/// <summary>Schema: ScanBackupsRequest.</summary>
public sealed record ScanBackupsRequest([property: JsonRequired] string TargetId);

/// <summary>Schema: BackupSnapshot.</summary>
public sealed record BackupSnapshot(string Id, DateTimeOffset StartTime, long TotalSize);

/// <summary>Schema: BackupCatalogVm.</summary>
/// <param name="Name">The name the VM had when last backed up.</param>
/// <param name="Snapshots">Newest first.</param>
public sealed record BackupCatalogVm(Guid VmId, IReadOnlyList<BackupSnapshot> Snapshots, string? Name = null);

/// <summary>What a target holds. Schema: BackupCatalog.</summary>
public sealed record BackupCatalog(IReadOnlyList<BackupCatalogVm> Vms, IReadOnlyList<BackupSnapshot> HostSnapshots);

/// <summary>Restores a snapshot as a new VM. Schema: RestoreVmRequest.</summary>
/// <param name="Name">A name for the VM; a taken name gets a suffix. Omitted: the name it had.</param>
/// <param name="Destination">The folder for the VM's files. Omitted: the host's VM folder.</param>
/// <param name="Start">Start the VM and wait for the guest.</param>
/// <param name="Administrator">With <paramref name="Start"/>, sets up the User's account again in the restored guest.</param>
public sealed record RestoreVmRequest(
    [property: JsonRequired] string TargetId,
    [property: JsonRequired] string SnapshotId,
    string? Name = null,
    string? Destination = null,
    bool Start = false,
    ProvisionVmRequest? Administrator = null);

/// <summary>Puts a snapshot's disks over a VM's disks. Schema: ReplaceDiskRequest.</summary>
/// <param name="TypedName">The VM's exact name, typed by the user.</param>
public sealed record ReplaceDiskRequest(
    [property: JsonRequired] string TargetId,
    [property: JsonRequired] string SnapshotId,
    [property: JsonRequired] string TypedName);

/// <summary>Schema: RestoreHostStateRequest.</summary>
public sealed record RestoreHostStateRequest([property: JsonRequired] string TargetId, [property: JsonRequired] string SnapshotId);

/// <summary>What a restore job did, once it succeeded. Schema: RestoreResult.</summary>
public sealed record RestoreResult(
    RestoreVmOutcome Outcome,
    IReadOnlyList<string> Warnings,
    bool NeedsDriverResync,
    bool BitLockerRecoveryMayBeNeeded,
    bool RestartRequired,
    Guid? VmId = null,
    string? VmName = null);
