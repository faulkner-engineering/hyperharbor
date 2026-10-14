using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.Backup;

/// <summary>
/// Decides which backup problems the tray announces: each failed run once, and each overdue item once a day. It starts
/// quiet: failures that were already in the history when the tray connected are remembered, not announced.
/// </summary>
internal sealed class BackupNotifier
{
    private readonly HashSet<Guid> _announcedRuns = [];
    private readonly Dictionary<string, DateOnly> _overdueDay = [];
    private bool _primed;

    public IReadOnlyList<(string Title, string Text)> Observe(TrayBackupStatus status, DateTimeOffset now)
    {
        var messages = new List<(string, string)>();
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        foreach (var item in status.Items)
        {
            if (item.Outcome == "failed" && item.RunId is { } run && _announcedRuns.Add(run) && _primed)
            {
                messages.Add(item.Name.StartsWith("Restore test of ", StringComparison.Ordinal)
                    ? ("A restore test failed", $"{item.Name.Replace("Restore test of ", "The latest backup of ", StringComparison.Ordinal)} on {item.TargetName} could not be restored: {Reason(item)}")
                    : ("A backup failed", $"{item.Name} to {item.TargetName}: {Reason(item)}"));
            }

            var key = item.Name + "\n" + item.TargetName;
            if (item.Overdue && (!_overdueDay.TryGetValue(key, out var day) || day != today))
            {
                _overdueDay[key] = today;
                if (_primed)
                {
                    messages.Add(("A backup is overdue", $"{item.Name} has no good backup on {item.TargetName} for a while."));
                }
            }
            else if (!item.Overdue)
            {
                _overdueDay.Remove(key);
            }
        }

        _primed = true;
        return messages;
    }

    private static string Reason(TrayBackupItem item) =>
        string.IsNullOrWhiteSpace(item.Reason) ? "It did not finish. The host log has details." : item.Reason;
}
