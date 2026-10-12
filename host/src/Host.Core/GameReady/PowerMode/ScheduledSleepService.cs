using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Core.Wake;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.GameReady.PowerMode;

/// <summary>
/// Answers the sleep task. It gathers what the host is doing, asks <see cref="ScheduledSleepGate"/>, and, when nothing
/// is in use, puts the host to sleep a moment later so the task can end first.
/// </summary>
public sealed class ScheduledSleepService(
    PowerModeService powerMode,
    ISleepController sleep,
    RemoteUseTracker tracker,
    HostActivity activity,
    IRemoteSessions sessions,
    ForegroundGameTracker game,
    KeepAwakeOptions options,
    TimeProvider time,
    ILogger<ScheduledSleepService> logger)
{
    public static readonly TimeSpan SleepDelay = TimeSpan.FromSeconds(3);

    /// <returns>Whether the host is going to sleep; if not, why, and whether the task should stop trying.</returns>
    public (bool Accepted, bool Final, string? Reason) Request()
    {
        activity.Observe();
        var inputs = new ScheduledSleepGate.Inputs(
            powerMode.Settings,
            time.GetLocalNow().DateTime,
            activity.InProgress,
            sessions.AnyActive(),
            tracker.LastUsed,
            time.GetUtcNow(),
            TimeSpan.FromMinutes(options.ClientWindowMinutes),
            game.Active);

        if (ScheduledSleepGate.Block(inputs) is { } reason)
        {
            logger.LogInformation("The scheduled sleep did not run: {Reason}", reason);
            return (false, ScheduledSleepGate.IsFinal(reason), reason);
        }

        logger.LogInformation("The scheduled sleep puts the host to sleep.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SleepDelay);
                sleep.Sleep();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                logger.LogWarning(ex, "The scheduled sleep failed.");
            }
        });
        return (true, true, null);
    }
}
