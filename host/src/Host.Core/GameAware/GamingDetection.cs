using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.GameAware;

/// <param name="Process">The program that triggered the verdict, or null.</param>
/// <param name="Reason">Why, in words for the log and the host window.</param>
public sealed record GamingVerdict(bool Gaming, string? Process, string Reason);

/// <summary>Decides from the foreground signals whether a game is in the foreground. Pure.</summary>
public static class GamingDetector
{
    public const int FullScreenD3d = 3;
    public const int PresentationMode = 4;

    /// <summary>Game launchers. A game started by one has it in its lineage; the launcher's own window is never the game.</summary>
    public static readonly IReadOnlyList<string> Launchers =
    [
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper", "battle.net", "agent", "xboxpcapp",
        "gamingservices", "gameservicesui", "galaxyclient", "galaxyclient helper", "playnite.desktopapp", "playnite.fullscreenapp",
    ];

    public static GamingVerdict Evaluate(ForegroundSignals signals, IReadOnlyCollection<string> allowlist, IReadOnlyCollection<string> ignoreList, IReadOnlyCollection<string>? extraLaunchers = null)
    {
        var name = signals.ProcessName;
        if (signals.IsOwnWindow)
        {
            return new GamingVerdict(false, name, "A HyperHarbor window is in the foreground.");
        }

        if (signals.IsShell)
        {
            return new GamingVerdict(false, name, "The desktop or taskbar is in the foreground.");
        }

        if (name is not null && ignoreList.Contains(name))
        {
            return new GamingVerdict(false, name, $"{name} is on the ignore list.");
        }

        if (name is not null && allowlist.Contains(name))
        {
            return new GamingVerdict(true, name, $"{name} is on the allowlist.");
        }

        var launchers = extraLaunchers is { Count: > 0 } ? Launchers.Concat(extraLaunchers).ToHashSet() : Launchers.ToHashSet();
        if (name is not null && launchers.Contains(name))
        {
            // The launcher's own window (its library, Big Picture) is not a game; the games it starts are.
            return new GamingVerdict(false, name, $"{name} is a game launcher.");
        }

        if (signals.NotificationState is FullScreenD3d)
        {
            return new GamingVerdict(true, name, "A full-screen Direct3D application is running.");
        }

        if (signals.NotificationState is PresentationMode)
        {
            return new GamingVerdict(true, name, "Windows is in presentation mode.");
        }

        if (signals.CoversMonitor && !signals.HasBorder)
        {
            if (signals.Lineage.FirstOrDefault(launchers.Contains) is { } launcher)
            {
                return new GamingVerdict(true, name, $"A borderless full-screen window started by {launcher}.");
            }

            return new GamingVerdict(false, name, "A borderless full-screen window that no game launcher started.");
        }

        return new GamingVerdict(false, name, "Nothing in the foreground looks like a game.");
    }
}

/// <summary>
/// Smooths the detector's answer: a game must hold for the enter delay before gaming starts, and be absent for the exit
/// delay before it ends, so alt-tabbing or a loading screen does not flap the VMs. Pure; the caller supplies the time.
/// </summary>
public sealed class GamingDebouncer(TimeSpan? enterAfter = null, TimeSpan? exitAfter = null)
{
    public static readonly TimeSpan DefaultEnter = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan DefaultExit = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _enter = enterAfter ?? DefaultEnter;
    private readonly TimeSpan _exit = exitAfter ?? DefaultExit;
    private DateTimeOffset? _changeSince;

    /// <summary>The state after smoothing.</summary>
    public bool Gaming { get; private set; }

    /// <summary>Feeds one sample and returns the smoothed state.</summary>
    public bool Observe(bool sample, DateTimeOffset now)
    {
        if (sample == Gaming)
        {
            _changeSince = null;
            return Gaming;
        }

        _changeSince ??= now;
        if (now - _changeSince >= (sample ? _enter : _exit))
        {
            Gaming = sample;
            _changeSince = null;
        }

        return Gaming;
    }

    public void Reset()
    {
        Gaming = false;
        _changeSince = null;
    }
}
