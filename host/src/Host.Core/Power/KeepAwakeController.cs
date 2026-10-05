using HyperHarbor.Host.Core.Updates;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Holds a power request while the host is used remotely and releases it afterwards, so Windows' sleep settings
/// (including the short unattended timeout after a Wake-on-LAN wake) apply only when nobody is using the host.
/// </summary>
public sealed class KeepAwakeController(
    KeepAwakeOptions options,
    RemoteUseTracker tracker,
    HostActivity activity,
    IRemoteSessions sessions,
    IPowerRequest power,
    TimeProvider time,
    ILogger<KeepAwakeController> logger)
{
    private KeepAwakeReason? _current;
    private bool _failed;

    /// <summary>The reason the host is being kept awake, or null when sleep is allowed.</summary>
    public KeepAwakeReason? Current => _current;

    /// <summary>Makes the decision again and changes the power request only when the decision changes.</summary>
    public void Tick()
    {
        if (!options.Enabled)
        {
            return;
        }

        activity.Observe();
        var now = time.GetUtcNow();
        var lastUsed = tracker.LastUsed;
        var reason = KeepAwakePolicy.Evaluate(now, lastUsed, TimeSpan.FromMinutes(options.ClientWindowMinutes), activity.InProgress, sessions.AnyActive());
        if (reason == _current && !_failed)
        {
            return;
        }

        try
        {
            if (reason is { } keep)
            {
                power.Set(KeepAwakePolicy.Describe(keep));
                logger.LogInformation("Keeping the host awake: {Reason}.", Explain(keep, now, lastUsed));
            }
            else
            {
                power.Clear();
                logger.LogInformation("Allowing sleep: no remote use for {Minutes} min.", options.ClientWindowMinutes);
            }

            _current = reason;
            _failed = false;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Try again at the next tick; a host that cannot hold the request still works, it just may sleep.
            _failed = true;
            logger.LogWarning("The keep-awake power request could not be changed: {Message}", ex.Message);
        }
    }

    /// <summary>Releases the power request, for example when the service stops.</summary>
    public void Release()
    {
        if (_current is null)
        {
            return;
        }

        power.Clear();
        _current = null;
    }

    private static string Explain(KeepAwakeReason reason, DateTimeOffset now, DateTimeOffset? lastUsed) => reason switch
    {
        KeepAwakeReason.WorkInProgress => "a request, console session, job, or pairing is in progress",
        KeepAwakeReason.RemoteDesktop => "someone is signed in to the host over Remote Desktop",
        _ => $"a paired device used it {(int)(now - lastUsed!.Value).TotalMinutes} min ago",
    };
}
