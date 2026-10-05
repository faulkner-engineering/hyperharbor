namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Settings for keeping the host awake while it is used remotely. Bound from the "KeepAwake" configuration section.
/// </summary>
public sealed class KeepAwakeOptions
{
    public const string SectionName = "KeepAwake";

    /// <summary>False leaves sleep entirely to Windows.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long after a paired device's last request the host stays awake.</summary>
    public int ClientWindowMinutes { get; set; } = 10;

    /// <summary>How often the decision is made again (a device's first request after a quiet spell also triggers it).</summary>
    public int PollSeconds { get; set; } = 30;
}

/// <summary>Why the host is being kept awake, in order of precedence.</summary>
public enum KeepAwakeReason
{
    /// <summary>A state-changing request, an open console tunnel, a running job, or a pending pairing.</summary>
    WorkInProgress,

    /// <summary>Someone is signed in to the host itself over Remote Desktop.</summary>
    RemoteDesktop,

    /// <summary>A paired device made a request recently (an open client checks in every few seconds).</summary>
    ClientActivity,
}

/// <summary>Decides whether the host should stay awake. Windows' own sleep settings apply whenever this returns null.</summary>
public static class KeepAwakePolicy
{
    public static KeepAwakeReason? Evaluate(DateTimeOffset now, DateTimeOffset? lastUsed, TimeSpan clientWindow, bool workInProgress, bool remoteSession)
    {
        if (workInProgress)
        {
            return KeepAwakeReason.WorkInProgress;
        }

        if (remoteSession)
        {
            return KeepAwakeReason.RemoteDesktop;
        }

        return lastUsed is { } used && now - used < clientWindow ? KeepAwakeReason.ClientActivity : null;
    }

    /// <summary>The text Windows shows for the power request (powercfg /requests).</summary>
    public static string Describe(KeepAwakeReason reason) => reason switch
    {
        KeepAwakeReason.WorkInProgress => "HyperHarbor: a paired device has work in progress on this host",
        KeepAwakeReason.RemoteDesktop => "HyperHarbor: someone is signed in to this host over Remote Desktop",
        _ => "HyperHarbor: a paired device is using this host",
    };
}

/// <summary>When a paired device last made a request. Every authenticated request counts, reads included.</summary>
public sealed class RemoteUseTracker(TimeProvider time)
{
    private long _lastUsedTicks;

    /// <summary>Raised when a request arrives after none for a while, so the keep-awake decision is made at once.</summary>
    public event Action? Resumed;

    public DateTimeOffset? LastUsed => Interlocked.Read(ref _lastUsedTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    public void MarkUsed()
    {
        var now = time.GetUtcNow().UtcTicks;
        var previous = Interlocked.Exchange(ref _lastUsedTicks, now);
        if (previous == 0 || TimeSpan.FromTicks(now - previous) > ResumeGap)
        {
            Resumed?.Invoke();
        }
    }

    /// <summary>A request after this long without one counts as use resuming (the client checks in every 5 s).</summary>
    public static readonly TimeSpan ResumeGap = TimeSpan.FromSeconds(30);
}

/// <summary>A Windows power request that keeps the system from sleeping while it is set.</summary>
public interface IPowerRequest
{
    /// <summary>Keeps the system awake, showing <paramref name="reason"/> in powercfg /requests; a second call updates the reason.</summary>
    void Set(string reason);

    void Clear();
}

/// <summary>Whether anyone is signed in to the host itself over Remote Desktop.</summary>
public interface IRemoteSessions
{
    bool AnyActive();
}
