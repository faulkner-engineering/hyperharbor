using System.Globalization;
using HyperHarbor.Host.Core.Power;

namespace HyperHarbor.Host.Core.GameReady.PowerMode;

/// <summary>The modes of the Power mode card. Exactly one is active; the value is stored and sent as written here.</summary>
public static class PowerModes
{
    public const string ConsoleIdle = "consoleIdle";
    public const string Balanced = "balanced";
    public const string SleepWhenIdle = "sleepWhenIdle";

    public static bool IsKnown(string? mode) => mode is ConsoleIdle or Balanced or SleepWhenIdle;
}

/// <summary>
/// The settings of one power plan that the modes change, on AC power. Timeouts are in seconds, 0 meaning never.
/// <see cref="WakeTimers"/> is the plan's "Allow wake timers" index: 0 disable, 1 enable, 2 important only.
/// <see cref="UnattendedSleepSeconds"/> is the hidden "System unattended sleep timeout" that puts a PC back to sleep a
/// couple of minutes after a timer woke it with nobody at the keyboard.
/// </summary>
public sealed record PowerValues(int SleepSeconds, int HibernateSeconds, int DisplaySeconds, int WakeTimers, int UnattendedSleepSeconds);

/// <summary>What the person chose on the card.</summary>
/// <param name="SleepTime">Local time of day (HH:mm) when the schedule puts the host to sleep.</param>
/// <param name="WakeTime">Local time of day (HH:mm) when the schedule wakes it.</param>
/// <param name="Watts">Estimated idle draw of the host, for the cost model.</param>
/// <param name="RatePerKwh">Price of one kilowatt hour, in the person's own currency.</param>
public sealed record PowerModeSettings(
    string Mode,
    int DisplayMinutes,
    bool ScheduleEnabled,
    string SleepTime,
    string WakeTime,
    double Watts,
    double RatePerKwh)
{
    public const int DefaultDisplayMinutes = 10;
    public const string DefaultSleepTime = "01:00";
    public const string DefaultWakeTime = "17:00";
    public const double DefaultWatts = 60;
    public const double DefaultRatePerKwh = 0.15;

    public static PowerModeSettings Default { get; } = new(
        PowerModes.Balanced, DefaultDisplayMinutes, false, DefaultSleepTime, DefaultWakeTime, DefaultWatts, DefaultRatePerKwh);

    /// <summary>Null when the settings are usable, else the reason they are not.</summary>
    public string? Validate()
    {
        if (!PowerModes.IsKnown(Mode))
        {
            return "Choose Console idle, Balanced, or Sleep when idle.";
        }

        if (DisplayMinutes is < 1 or > 720)
        {
            return "The display timeout must be between 1 and 720 minutes.";
        }

        if (!double.IsFinite(Watts) || Watts is < 1 or > 2000)
        {
            return "The idle power draw must be between 1 and 2000 watts.";
        }

        if (!double.IsFinite(RatePerKwh) || RatePerKwh is < 0 or > 100)
        {
            return "The electricity rate must be between 0 and 100 per kWh.";
        }

        if (ParseTime(SleepTime) is null || ParseTime(WakeTime) is null)
        {
            return "Times must look like 01:00 or 17:30.";
        }

        if (ScheduleEnabled && SleepTime == WakeTime)
        {
            return "The sleep time and the wake time must differ.";
        }

        return null;
    }

    public static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
}

/// <summary>
/// What is saved between runs: the chosen settings, the plan they were applied to, and that plan's values from before
/// the first non-Balanced mode was applied (null in Balanced). Reverting writes <see cref="Previous"/> back.
/// </summary>
public sealed record PowerModeRecord(PowerModeSettings Settings, Guid? PlanId, PowerValues? Previous);

/// <summary>Where the record lives. The data folder's own protection applies; the record holds no secret.</summary>
public interface IPowerModeStore
{
    PowerModeRecord? Load();

    void Save(PowerModeRecord record);
}

/// <summary>The active power plan's settings. A plan is changed in place; none is created.</summary>
public interface IPowerPlans
{
    Guid ActivePlan();

    /// <returns>The plan's AC values, or null when they cannot be read.</returns>
    PowerValues? Read(Guid plan);

    /// <summary>Writes the AC values and applies the plan again. Needs administrator rights.</summary>
    /// <exception cref="InvalidOperationException">powercfg refused.</exception>
    void Write(Guid plan, PowerValues values);
}

/// <summary>The two scheduled tasks of the sleep schedule.</summary>
public interface IPowerSchedule
{
    bool IsRegistered();

    /// <summary>Registers (or replaces) the sleep task and the wake task. Needs administrator rights.</summary>
    /// <exception cref="InvalidOperationException">The tasks could not be registered.</exception>
    void Register(TimeOnly sleepAt, TimeOnly wakeAt);

    /// <summary>Removes both tasks. Missing tasks are not an error.</summary>
    void Remove();
}

public sealed class PowerModeException(string message) : Exception(message);

/// <summary>The model behind the monthly cost line.</summary>
/// <param name="AlwaysReadyMonthlyCost">The host awake all day.</param>
/// <param name="ScheduledMonthlyCost">The host asleep between the sleep time and the wake time.</param>
public sealed record PowerEstimate(double Watts, double AlwaysReadyKwh, double AlwaysReadyMonthlyCost, double ScheduledKwh, double ScheduledMonthlyCost);

public static class PowerCostModel
{
    /// <summary>Assumed draw while the host sleeps. A fixed figure; the card says so.</summary>
    public const double SleepWatts = 3;

    private const double HoursPerMonth = 730;

    public static PowerEstimate Estimate(PowerModeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var alwaysKwh = settings.Watts * HoursPerMonth / 1000;
        var asleep = AsleepFraction(settings.SleepTime, settings.WakeTime);
        var scheduledKwh = (settings.Watts * (1 - asleep) + SleepWatts * asleep) * HoursPerMonth / 1000;
        return new PowerEstimate(
            settings.Watts,
            Math.Round(alwaysKwh, 1),
            Math.Round(alwaysKwh * settings.RatePerKwh, 2),
            Math.Round(scheduledKwh, 1),
            Math.Round(scheduledKwh * settings.RatePerKwh, 2));
    }

    /// <summary>The part of a day between the sleep time and the next wake time.</summary>
    internal static double AsleepFraction(string sleepTime, string wakeTime)
    {
        if (PowerModeSettings.ParseTime(sleepTime) is not { } sleep || PowerModeSettings.ParseTime(wakeTime) is not { } wake)
        {
            return 0;
        }

        var span = wake.ToTimeSpan() - sleep.ToTimeSpan();
        if (span < TimeSpan.Zero)
        {
            span += TimeSpan.FromDays(1);
        }

        return span.TotalDays;
    }
}

/// <summary>
/// Decides whether the scheduled sleep may put the host to sleep now. Pure: the service gathers the inputs.
/// Anything that might be in use blocks it; the sleep task tries again later.
/// </summary>
public static class ScheduledSleepGate
{
    public sealed record Inputs(
        PowerModeSettings Settings,
        DateTime LocalNow,
        bool WorkInProgress,
        bool RemoteSession,
        DateTimeOffset? LastClientUse,
        DateTimeOffset Now,
        TimeSpan ClientWindow,
        bool GameInForeground);

    /// <summary>Why the host stays awake, or null when it may sleep. <see cref="NotScheduled"/> and <see cref="OutsideWindow"/> are final, not retried.</summary>
    public static string? Block(Inputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var settings = inputs.Settings;
        if (settings.Mode != PowerModes.ConsoleIdle || !settings.ScheduleEnabled)
        {
            return NotScheduled;
        }

        if (!InWindow(settings, TimeOnly.FromDateTime(inputs.LocalNow)))
        {
            return OutsideWindow;
        }

        if (KeepAwakePolicy.Evaluate(inputs.Now, inputs.LastClientUse, inputs.ClientWindow, inputs.WorkInProgress, inputs.RemoteSession) is { } reason)
        {
            return KeepAwakePolicy.Describe(reason);
        }

        return inputs.GameInForeground ? "A game is running in the foreground." : null;
    }

    public const string NotScheduled = "The sleep schedule is not turned on.";
    public const string OutsideWindow = "It is outside the sleep hours.";

    /// <summary>True from the sleep time up to, not including, the wake time. The window may cross midnight.</summary>
    public static bool InWindow(PowerModeSettings settings, TimeOnly time)
    {
        if (PowerModeSettings.ParseTime(settings.SleepTime) is not { } sleep || PowerModeSettings.ParseTime(settings.WakeTime) is not { } wake)
        {
            return false;
        }

        return sleep < wake ? time >= sleep && time < wake : time >= sleep || time < wake;
    }

    /// <summary>True for the two reasons that are final, so the sleep task has no reason to try again.</summary>
    public static bool IsFinal(string reason) => reason is NotScheduled or OutsideWindow;
}
