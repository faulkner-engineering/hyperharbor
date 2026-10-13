using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>Builds the backup summary the tray and the ambient screen show. Pure.</summary>
public static class BackupTrayStatus
{
    public static TrayBackupStatus Build(
        BackupConfig config, BackupHistoryStore history, IReadOnlyDictionary<Guid, string> vmNames, DateTimeOffset now, bool running, int waiting)
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

    private static string OutcomeName(BackupOutcome outcome) => outcome switch
    {
        BackupOutcome.Failed => "failed",
        BackupOutcome.Skipped => "skipped",
        BackupOutcome.Partial => "partial",
        _ => "succeeded",
    };
}
