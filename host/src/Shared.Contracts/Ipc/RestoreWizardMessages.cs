namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>
/// Where the backups are and the recovery key that opens them, typed into the host window's Restore page on a new PC.
/// Kind is "local", "s3", or "azure"; only that kind's fields are used. Holds secrets, so it never prints them.
/// </summary>
public sealed record RestoreWizardForm(
    string Kind,
    string Name,
    string RecoveryKey,
    string? Path = null,
    string? Endpoint = null,
    string? Bucket = null,
    string? Region = null,
    string? Prefix = null,
    bool UseTls = true,
    string? AccessKey = null,
    string? SecretKey = null,
    string? AzureAccount = null,
    string? AzureContainer = null,
    string? AzureKey = null)
{
    public override string ToString() => $"RestoreWizardForm {{ Kind = {Kind}, Name = {Name}, ***** }}";
}

public sealed record RestoreWizardSnapshot(string Id, DateTimeOffset StartTime, long TotalSize);

public sealed record RestoreWizardVm(Guid VmId, string? Name, IReadOnlyList<RestoreWizardSnapshot> Snapshots);

/// <summary>
/// The Restore page's state. Phase is "idle" (ask for the target), "attaching" (checking the key and listing backups), "ready"
/// (choose what to restore), or "restoring". <see cref="Messages"/> are the outcome lines of the last restore.
/// </summary>
public sealed record RestoreWizardStatus(
    string Phase,
    string Step,
    int Percent,
    string? Error,
    string? TargetName,
    IReadOnlyList<RestoreWizardVm> Vms,
    IReadOnlyList<RestoreWizardSnapshot> HostSnapshots,
    IReadOnlyList<string> Messages,
    bool RestartRequired);

/// <summary>Asks for <see cref="RestoreWizardStatusMessage"/>.</summary>
public sealed record RestoreWizardQueryMessage : TrayMessage;

/// <summary>Checks the recovery key against the storage, keeps the target, and lists what it holds.</summary>
public sealed record RestoreWizardAttachMessage(RestoreWizardForm Form) : TrayMessage;

/// <summary>
/// Restores one snapshot. Kind is "host" (settings, pairings, certificate) or "vm" (a new VM; <paramref name="Name"/> is optional).
/// </summary>
public sealed record RestoreWizardRestoreMessage(string Kind, string SnapshotId, string? Name = null, bool Start = false) : TrayMessage;

/// <summary>Forgets the attached target's listing and returns to the first step. The target itself is kept.</summary>
public sealed record RestoreWizardResetMessage : TrayMessage;

public sealed record RestoreWizardStatusMessage(RestoreWizardStatus Status) : TrayMessage;
