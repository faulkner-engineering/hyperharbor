using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.GameReady.PowerMode;

/// <summary>What the card shows: the saved settings and how the active plan compares with them.</summary>
/// <param name="Applied">True when the active plan holds the values the mode calls for.</param>
public sealed record PowerModeReading(
    PowerModeSettings Settings,
    Guid? ActivePlan,
    PowerValues? Current,
    PowerValues? Expected,
    bool ScheduleRegistered,
    bool Applied)
{
    public bool ScheduleExpected => Settings.Mode == PowerModes.ConsoleIdle && Settings.ScheduleEnabled;

    /// <summary>The schedule is in the state the settings call for.</summary>
    public bool ScheduleInPlace => ScheduleRegistered == ScheduleExpected;
}

/// <summary>
/// Applies the three power modes to the active power plan, in place, so the plan's GPU vendor and launcher tweaks
/// stay. Applying is idempotent. The first change from Balanced records the plan's values, and Balanced writes them back.
/// </summary>
public sealed class PowerModeService(IPowerPlans plans, IPowerSchedule schedule, IPowerModeStore store, ILogger<PowerModeService> logger)
{
    /// <summary>Sleep when idle: short timers for a desk PC.</summary>
    public static readonly PowerValues SleepWhenIdleTimers = new(15 * 60, 0, 5 * 60, 0, 120);

    private readonly object _gate = new();

    public PowerModeSettings Settings => (store.Load() ?? new PowerModeRecord(PowerModeSettings.Default, null, null)).Settings;

    public PowerModeReading Read()
    {
        var record = store.Load() ?? new PowerModeRecord(PowerModeSettings.Default, null, null);
        Guid? active = null;
        PowerValues? current = null;
        try
        {
            active = plans.ActivePlan();
            current = plans.Read(active.Value);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "The active power plan could not be read.");
        }

        var expected = current is null ? null : Desired(record.Settings, record.Previous ?? current);
        var applied = record.Settings.Mode == PowerModes.Balanced
            ? record.Previous is null
            : record.PlanId == active && current is not null && current == expected;
        return new PowerModeReading(record.Settings, active, current, expected, SafeIsRegistered(), applied);
    }

    /// <summary>Applies the settings. A failure puts back what this call changed.</summary>
    /// <exception cref="PowerModeException">The settings are not valid, or a step failed; the message says why.</exception>
    public void Apply(PowerModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Validate() is { } invalid)
        {
            throw new PowerModeException(invalid);
        }

        lock (_gate)
        {
            var record = store.Load() ?? new PowerModeRecord(PowerModeSettings.Default, null, null);
            var active = Try(plans.ActivePlan, "read the active power plan");
            var undo = new Stack<(string What, Action Action)>();
            try
            {
                var previous = record.Previous;
                var planId = record.PlanId;

                // The mode was applied to another plan: put that plan back first, then start from the active one.
                if (previous is not null && planId is { } old && old != active)
                {
                    var oldCurrent = Try(() => plans.Read(old), "read the earlier power plan");
                    Try(() => plans.Write(old, previous), "restore the earlier power plan");
                    undo.Push(("rewrite the earlier plan", () => plans.Write(old, oldCurrent ?? previous)));
                    previous = null;
                }

                var current = Try(() => plans.Read(active), "read the power plan") ?? throw new PowerModeException("The power plan's settings could not be read.");
                if (settings.Mode == PowerModes.Balanced)
                {
                    var baseline = previous ?? current;
                    WriteIfDifferent(active, current, baseline, undo);
                    previous = null;
                }
                else
                {
                    previous ??= current;
                    WriteIfDifferent(active, current, Desired(settings, previous), undo);
                }

                ApplySchedule(settings, undo);
                store.Save(new PowerModeRecord(settings, previous is null ? null : active, previous));
                logger.LogInformation("Power mode {Mode} applied to power plan {Plan}.", settings.Mode, active);
            }
            catch (Exception ex) when (ex is PowerModeException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                var problems = Undo(undo);
                var message = ex is PowerModeException ? ex.Message : $"The power mode could not be applied: {ex.Message}";
                throw new PowerModeException(problems.Count == 0 ? message : $"{message} Could not be put back: {string.Join("; ", problems)}.");
            }
        }
    }

    /// <summary>The values a mode calls for. <paramref name="baseline"/> supplies the settings the mode leaves alone.</summary>
    internal static PowerValues Desired(PowerModeSettings settings, PowerValues baseline) => settings.Mode switch
    {
        PowerModes.ConsoleIdle => new PowerValues(0, 0, settings.DisplayMinutes * 60, settings.ScheduleEnabled ? 1 : baseline.WakeTimers, 0),
        PowerModes.SleepWhenIdle => SleepWhenIdleTimers with { WakeTimers = baseline.WakeTimers, UnattendedSleepSeconds = baseline.UnattendedSleepSeconds },
        _ => baseline,
    };

    private void WriteIfDifferent(Guid plan, PowerValues current, PowerValues desired, Stack<(string What, Action Action)> undo)
    {
        if (current == desired)
        {
            return;
        }

        plans.Write(plan, desired);
        undo.Push(("restore the power plan", () => plans.Write(plan, current)));
    }

    private void ApplySchedule(PowerModeSettings settings, Stack<(string What, Action Action)> undo)
    {
        var wanted = settings.Mode == PowerModes.ConsoleIdle && settings.ScheduleEnabled;
        if (wanted)
        {
            schedule.Register(PowerModeSettings.ParseTime(settings.SleepTime)!.Value, PowerModeSettings.ParseTime(settings.WakeTime)!.Value);
            var before = store.Load();
            undo.Push(("restore the sleep schedule", () =>
            {
                if (before is { Settings: { Mode: PowerModes.ConsoleIdle, ScheduleEnabled: true } earlier })
                {
                    schedule.Register(PowerModeSettings.ParseTime(earlier.SleepTime)!.Value, PowerModeSettings.ParseTime(earlier.WakeTime)!.Value);
                }
                else
                {
                    schedule.Remove();
                }
            }));
        }
        else if (SafeIsRegistered())
        {
            var before = store.Load();
            schedule.Remove();
            undo.Push(("restore the sleep schedule", () =>
            {
                if (before is { Settings: { ScheduleEnabled: true } earlier })
                {
                    schedule.Register(PowerModeSettings.ParseTime(earlier.SleepTime)!.Value, PowerModeSettings.ParseTime(earlier.WakeTime)!.Value);
                }
            }));
        }
    }

    private List<string> Undo(Stack<(string What, Action Action)> undo)
    {
        var problems = new List<string>();
        while (undo.Count > 0)
        {
            var (what, action) = undo.Pop();
            try
            {
                action();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Power mode undo failed: {What}.", what);
                problems.Add(what);
            }
        }

        return problems;
    }

    private bool SafeIsRegistered()
    {
        try
        {
            return schedule.IsRegistered();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static T Try<T>(Func<T> action, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new PowerModeException($"Could not {what}: {ex.Message}");
        }
    }

    private static void Try(Action action, string what) => Try<object?>(() =>
    {
        action();
        return null;
    }, what);
}
