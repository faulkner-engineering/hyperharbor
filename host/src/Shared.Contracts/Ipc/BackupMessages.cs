namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>How one backed-up item (the host's settings or a VM) did on one target, as the tray shows it.</summary>
/// <param name="Name">The VM's name, or "Host settings".</param>
/// <param name="Outcome">The last run: succeeded, partial, skipped, or failed; null when it never ran.</param>
/// <param name="FinishedAt">When the last run ended.</param>
/// <param name="Reason">Why the last run failed or was skipped.</param>
/// <param name="LastSuccessAt">The last run that stored a backup.</param>
/// <param name="Overdue">The last success is older than twice the schedule's longest gap.</param>
/// <param name="RunId">The last run's id, so the tray notifies once per failure.</param>
public sealed record TrayBackupItem(
    string Name,
    string TargetName,
    string? Outcome,
    DateTimeOffset? FinishedAt,
    string? Reason,
    DateTimeOffset? LastSuccessAt,
    bool Overdue,
    Guid? RunId);

/// <summary>The backup section of the host window, and the source of its failure notifications.</summary>
/// <param name="Configured">At least one usable target exists.</param>
/// <param name="PendingKeys">Targets that wait for their recovery key to be confirmed.</param>
public sealed record TrayBackupStatus(bool Configured, int PendingKeys, bool Running, int Waiting, IReadOnlyList<TrayBackupItem> Items);

/// <summary>Asks for <see cref="BackupStatusMessage"/>.</summary>
public sealed record BackupStatusQueryMessage : TrayMessage;

public sealed record BackupStatusMessage(TrayBackupStatus Status) : TrayMessage;

/// <summary>"Back up now" from the host window: runs everything that is backed up.</summary>
public sealed record BackupRunNowMessage : TrayMessage;

/// <summary>The answer to <see cref="BackupRunNowMessage"/>.</summary>
/// <param name="Problems">One sentence for each run that could not start.</param>
public sealed record BackupRunNowResultMessage(int Started, IReadOnlyList<string> Problems) : TrayMessage;
