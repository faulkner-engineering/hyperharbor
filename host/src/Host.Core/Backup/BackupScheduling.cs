using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>What the run conditions are checked against.</summary>
/// <param name="VmSessionActive">Someone is using the VM (null when the item is the host's own state).</param>
/// <param name="GameActive">A game runs in the foreground on the host.</param>
/// <param name="LocalTime">The host's local time of day.</param>
public sealed record ConditionInputs(bool? VmSessionActive, bool GameActive, TimeOnly LocalTime);

public sealed record ConditionResult(bool Allowed, string? Reason)
{
    public static ConditionResult Yes { get; } = new(true, null);
}

/// <summary>Decides whether a scheduled or shutdown-triggered run may start now. Pure.</summary>
public static class RunConditionEvaluator
{
    public static ConditionResult Evaluate(RunConditions conditions, ConditionInputs inputs)
    {
        if (conditions.NoActiveSession && inputs.VmSessionActive == true)
        {
            return new ConditionResult(false, "A session is active on the virtual machine.");
        }

        if (conditions.NotInGame && inputs.GameActive)
        {
            return new ConditionResult(false, "A game is running on the host.");
        }

        if (conditions.Window is { } window && !InWindow(window, inputs.LocalTime))
        {
            return new ConditionResult(false, $"Outside the allowed time window {window.Start:HH\\:mm} to {window.End:HH\\:mm}.");
        }

        return ConditionResult.Yes;
    }

    /// <summary>Start inclusive, end exclusive; a window whose end is before its start crosses midnight. Equal times mean all day.</summary>
    public static bool InWindow(TimeWindow window, TimeOnly time)
    {
        if (window.Start == window.End)
        {
            return true;
        }

        return window.Start < window.End
            ? time >= window.Start && time < window.End
            : time >= window.Start || time < window.End;
    }
}

/// <summary>Works out whether a target's schedule has come due. Pure.</summary>
public static class ScheduleCalculator
{
    /// <summary>The latest scheduled moment at or before <paramref name="now"/> (looking back up to a week), or null when none.</summary>
    public static DateTimeOffset? LatestSlot(ScheduleSpec schedule, DateTimeOffset now)
    {
        for (var back = 0; back <= 7; back++)
        {
            var date = DateOnly.FromDateTime(now.Date).AddDays(-back);
            if (schedule.Days is { Count: > 0 } days && !days.Contains(date.DayOfWeek))
            {
                continue;
            }

            var local = date.ToDateTime(schedule.Time);
            var slot = new DateTimeOffset(local, now.Offset);
            if (slot <= now)
            {
                return slot;
            }
        }

        return null;
    }

    /// <summary>Due when the latest slot has passed and nothing was attempted since it (a missed slot runs when the host is next up).</summary>
    public static bool IsDue(ScheduleSpec schedule, DateTimeOffset now, DateTimeOffset? lastAttempt)
    {
        var slot = LatestSlot(schedule, now);
        return slot is not null && (lastAttempt is null || lastAttempt < slot);
    }
}

/// <summary>
/// Turns "a VM went from running to off" into one backup after a quiet period. A start within the period cancels it, another
/// shutdown restarts the period, so a restart runs at most once. Pure; the caller feeds it observed states.
/// </summary>
public sealed class ShutdownDebouncer
{
    private readonly Dictionary<Guid, VmState> _last = [];
    private readonly Dictionary<Guid, DateTimeOffset> _due = [];

    public int PendingCount => _due.Count;

    /// <param name="debounce">The VM's debounce period; a policy without the shutdown trigger passes null and schedules nothing.</param>
    public void Observe(Guid vmId, VmState state, DateTimeOffset now, TimeSpan? debounce)
    {
        var wasRunning = _last.TryGetValue(vmId, out var previous) && previous != VmState.Off;
        _last[vmId] = state;
        if (debounce is null)
        {
            _due.Remove(vmId);
            return;
        }

        if (state == VmState.Off)
        {
            // The first observation is only a baseline: a VM found off at startup did not just shut down.
            if (wasRunning)
            {
                _due[vmId] = now + debounce.Value;
            }
        }
        else
        {
            _due.Remove(vmId);
        }
    }

    /// <summary>The VMs whose quiet period has ended; each is returned once.</summary>
    public IReadOnlyList<Guid> TakeDue(DateTimeOffset now)
    {
        var ready = _due.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList();
        foreach (var id in ready)
        {
            _due.Remove(id);
        }

        return ready;
    }

    /// <summary>Forgets a VM that no longer exists.</summary>
    public void Forget(Guid vmId)
    {
        _last.Remove(vmId);
        _due.Remove(vmId);
    }
}

/// <summary>Whether someone is using a VM right now.</summary>
public interface IVmSessionProbe
{
    Task<bool> HasActiveSessionAsync(Vm vm, CancellationToken cancellationToken);
}
