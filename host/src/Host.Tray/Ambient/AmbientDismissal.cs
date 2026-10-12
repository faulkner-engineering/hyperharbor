using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>One reading of the game controllers: a number that changes when any of them changes, and each pad's button word.</summary>
internal readonly record struct ControllerSample(long Token, IReadOnlyList<ushort> Pads);

/// <summary>The controllers' buttons. Null when no controller is connected.</summary>
internal interface IControllerButtons
{
    ControllerSample? Sample();
}

/// <summary>The tray's side of the conversation with a running ambient screen.</summary>
internal interface IAmbientSignals
{
    /// <summary>True while the ambient screensaver process runs on this desktop.</summary>
    bool Running { get; }

    /// <summary>Asks the screen to end.</summary>
    void Dismiss();

    /// <summary>Asks the screen to show the unlock chord hint for a moment.</summary>
    void ShowHint();
}

/// <summary>
/// Ends the ambient screen when a controller is used. Windows ends a screensaver on keyboard and mouse input by itself,
/// but XInput does not count as input to Windows, so the tray watches the controllers while the screen runs.
/// Three rules shape it:
/// <list type="bullet">
/// <item>While the display is off the controller only wakes the display (<see cref="DisplayWake"/>); it does not end the screen.</item>
/// <item>After the display comes on, the screen stays for <see cref="WakeGrace"/>, so the press that woke the TV does not skip it.</item>
/// <item>With the soft lock on, only the chord ends the screen; any other change shows the chord hint instead.</item>
/// </list>
/// </summary>
internal sealed class AmbientDismissal(IControllerButtons input, IAmbientSignals signals, TimeProvider time)
{
    /// <summary>How long the screen stays after the display comes on.</summary>
    public static readonly TimeSpan WakeGrace = TimeSpan.FromSeconds(3);

    /// <summary>A second hint within this time is not asked for again.</summary>
    public static readonly TimeSpan HintGap = TimeSpan.FromSeconds(2);

    private long? _last;
    private bool _displayOff;
    private DateTimeOffset _graceUntil;
    private DateTimeOffset? _lastHint;

    /// <summary>True in Console idle with the ambient screen on.</summary>
    public bool Enabled { get; set; }

    public bool SoftLock { get; set; }

    /// <summary>The chord's XInput bits; ignored unless <see cref="SoftLock"/> is on.</summary>
    public ushort Chord { get; set; }

    /// <summary>True while the display is off or dimmed. The change to on starts the wake grace.</summary>
    public bool DisplayOff
    {
        get => _displayOff;
        set
        {
            if (_displayOff && !value)
            {
                _graceUntil = time.GetUtcNow() + WakeGrace;
            }

            _displayOff = value;
        }
    }

    /// <summary>How many times the screen was asked to end because of a controller.</summary>
    public int Dismissed { get; private set; }

    /// <summary>How many times the chord hint was asked for.</summary>
    public int Hints { get; private set; }

    /// <summary>Called a few times a second.</summary>
    public void Tick()
    {
        if (!Enabled || _displayOff || !signals.Running)
        {
            // The next time the screen runs, the state at that moment is the starting point.
            _last = null;
            return;
        }

        var sample = input.Sample();
        if (sample is not { } current)
        {
            _last = null;
            return;
        }

        var changed = _last is { } previous && previous != current.Token;
        _last = current.Token;
        if (!changed || time.GetUtcNow() < _graceUntil)
        {
            return;
        }

        if (SoftLock && !AmbientChord.IsHeld(current.Pads, Chord))
        {
            var now = time.GetUtcNow();
            if (_lastHint is not { } at || now - at >= HintGap)
            {
                _lastHint = now;
                Hints++;
                signals.ShowHint();
            }

            return;
        }

        Dismissed++;
        signals.Dismiss();
    }
}
