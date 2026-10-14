using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>Builds the backup summary the tray and the ambient screen show. Pure.</summary>
public static class BackupTrayStatus
{
    public static TrayBackupStatus Build(
        BackupConfig config, BackupHistoryStore history, IReadOnlyDictionary<Guid, string> vmNames, DateTimeOffset now, bool running, int waiting,
        IEnumerable<RestoreTestRecord>? restoreTests = null)
    {
        var items = BackupStatusBuilder.Build(config, history, vmNames, now)
            .Select(item => new TrayBackupItem(
                item.Item == BackupCoordinator.HostItem ? "Host settings" : item.VmName ?? "A virtual machine",
                item.TargetName,
                item.LastRun is null ? null : OutcomeName(item.LastRun.Outcome),
                item.LastRun?.FinishedAt,
                item.LastRun?.Reason ?? item.LastRun?.Warnings.FirstOrDefault(),
                item.LastSuccessAt,
                item.Overdue,
                item.LastRun?.RunId))
            .ToList();
        items.AddRange(FailedRestoreTests(config, restoreTests ?? []));
        var ready = config.Targets.Count(t => t.Enabled && t.VmRepository?.KeyState == RepositoryKeyState.Confirmed);
        var pending = config.Targets.Count(t => t.VmRepository?.KeyState != RepositoryKeyState.Confirmed);
        return new TrayBackupStatus(ready > 0, pending, running, waiting, items);
    }

    /// <summary>One word for the ambient screen: ok, attention (a failure or an overdue item), or none (nothing configured yet).</summary>
    public static string Summary(TrayBackupStatus status)
    {
        if (!status.Configured || status.Items.Count == 0)
        {
            return "none";
        }

        return status.Items.Any(item => item.Overdue || item.Outcome == "failed") ? "attention" : "ok";
    }

    public const string RestoreTestPrefix = "Restore test of ";

    /// <summary>
    /// A VM whose latest restore test failed counts as a failed item, so the card, the notification, and the ambient summary
    /// treat a backup that cannot be restored like one that did not run. A later pass or skip clears it.
    /// </summary>
    private static IEnumerable<TrayBackupItem> FailedRestoreTests(BackupConfig config, IEnumerable<RestoreTestRecord> records) =>
        records
            .GroupBy(r => r.VmId)
            .Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .Where(r => r.Outcome == RestoreTestOutcome.Failed)
            .Select(r => new TrayBackupItem(
                RestoreTestPrefix + r.VmName,
                config.Targets.FirstOrDefault(t => t.Id == r.TargetId)?.Name ?? r.TargetId,
                "failed",
                r.FinishedAt,
                r.Reason,
                null,
                false,
                r.RunId));

    private static string OutcomeName(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Failed => "failed",
        BackupOutcome.Skipped => "skipped",
        BackupOutcome.Partial => "partial",
        _ => "succeeded",
    };
}
