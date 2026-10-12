using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.GameReady.Ambient;

/// <summary>
/// What the person chose for the Ambient screen. The screen belongs to Console idle: it starts after
/// <see cref="IdleMinutes"/> without input and gives way to the display-off timeout of the power plan.
/// </summary>
/// <param name="SoftLock">Dismissing the screen with a controller needs <paramref name="Chord"/>. This is not a security boundary.</param>
public sealed record AmbientSettings(
    bool Enabled,
    int IdleMinutes,
    bool ShowHostStatus,
    bool ShowRunningVms,
    bool ShowDevices,
    bool SoftLock,
    string Chord)
{
    public const int DefaultIdleMinutes = 5;
    public const int MaximumIdleMinutes = 120;

    public static AmbientSettings Default { get; } = new(false, DefaultIdleMinutes, true, true, false, false, AmbientChord.Default);

    /// <summary>Null when the settings are usable with this power mode, else the reason they are not.</summary>
    public string? Validate(PowerModeSettings power)
    {
        ArgumentNullException.ThrowIfNull(power);

        if (IdleMinutes is < 1 or > MaximumIdleMinutes)
        {
            return $"The ambient screen must start after 1 to {MaximumIdleMinutes} minutes.";
        }

        if (AmbientChord.Parse(Chord) is null)
        {
            return "The unlock chord must be two to four distinct buttons such as lb+rb+start.";
        }

        if (!Enabled)
        {
            return null;
        }

        if (power.Mode != PowerModes.ConsoleIdle)
        {
            return "The ambient screen belongs to Console idle. Apply that power mode first.";
        }

        return AmbientTimeouts.OrderProblem(IdleMinutes * 60, power.DisplayMinutes * 60);
    }
}

/// <summary>The ordering rule between the two idle timers: the ambient screen must start before the display turns off.</summary>
public static class AmbientTimeouts
{
    /// <summary>Null when <paramref name="screensaverSeconds"/> is shorter than <paramref name="displaySeconds"/> (0 for the display means never), else why not.</summary>
    public static string? OrderProblem(int screensaverSeconds, int displaySeconds)
    {
        if (screensaverSeconds <= 0)
        {
            return "The screensaver has no timeout, so it would never start.";
        }

        if (displaySeconds > 0 && screensaverSeconds >= displaySeconds)
        {
            return $"The ambient screen ({Duration(screensaverSeconds)}) must start before the display turns off ({Duration(displaySeconds)}). Choose a shorter screen time or a longer display timeout.";
        }

        return null;
    }

    private static string Duration(int seconds) => seconds % 60 == 0 ? $"{seconds / 60} min" : $"{seconds} s";
}

/// <summary>Where the choices live (ambient.json in the data folder). The record holds no secret.</summary>
public interface IAmbientStore
{
    AmbientSettings? Load();

    void Save(AmbientSettings settings);
}

/// <summary>The screensaver executable Windows starts (a copy of the host executable named .scr).</summary>
public interface IAmbientScreensaverFile
{
    /// <summary>Installs or refreshes the file and returns its path.</summary>
    /// <exception cref="AmbientException">The file cannot be installed here; the message says why.</exception>
    string Ensure();

    /// <summary>The path when the file is installed and current, else null.</summary>
    string? CurrentPath();

    /// <summary>Null when the file can be installed on this host, else why not.</summary>
    string? UnsupportedReason();
}

public sealed class AmbientException(string message) : Exception(message);

/// <summary>The Ambient screen as the service sees it: the saved choices, whether the file is in place, and a CEC adapter if present.</summary>
public sealed record AmbientReading(AmbientSettings Settings, bool Supported, string? UnsupportedReason, string? ScreensaverPath, string? CecDevice);
