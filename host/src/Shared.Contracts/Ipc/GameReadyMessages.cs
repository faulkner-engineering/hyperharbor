namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>The cards of the tray's Game ready section. Stable: the page keys its actions on them.</summary>
public static class GameReadyCardIds
{
    public const string WakeOnLan = "wakeOnLan";
    public const string ControllerWake = "controllerWake";
    public const string FastStartup = "fastStartup";
    public const string UsbSelectiveSuspend = "usbSelectiveSuspend";
    public const string UsbPowerInSleep = "usbPowerInSleep";
    public const string SleepPolicy = "sleepPolicy";
    public const string BootToLauncher = "bootToLauncher";
    public const string SteamDesktopLayout = "steamDesktopLayout";
    public const string LeanHost = "leanHost";
    public const string GameAwareMode = "gameAwareMode";

    /// <summary>The cards whose Fix action exists.</summary>
    public static IReadOnlySet<string> Fixable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        WakeOnLan,
        ControllerWake,
        FastStartup,
        UsbSelectiveSuspend,
    };
}

/// <summary>How a card stands. A card that cannot be evaluated is unavailable; one the user checks by hand is manual.</summary>
public static class GameReadyStates
{
    public const string Ready = "ready";
    public const string Attention = "attention";
    public const string Manual = "manual";
    public const string Unavailable = "unavailable";
    public const string ComingSoon = "comingSoon";
}

public static class GameReadyCheckStatuses
{
    public const string Pass = "pass";
    public const string Warn = "warn";
    public const string Fail = "fail";
}

/// <summary>What a card is working on, in <see cref="GameReadyStatus.Busy"/> (fix:&lt;card id&gt; while a Fix runs).</summary>
public static class GameReadyBusy
{
    public const string Reading = "reading";
    public const string FixPrefix = "fix:";
}

/// <summary>Whether a game controller or receiver can wake the PC. Armed, Can arm, and Cannot wake on the page.</summary>
public static class ControllerWakeStates
{
    public const string Armed = "armed";
    public const string CanArm = "canArm";
    public const string CannotWake = "cannotWake";
}

public static class ControllerKinds
{
    public const string Controller = "controller";
    public const string Receiver = "receiver";
}

/// <summary>How the device reaches the PC. Receiver means a controller reached through a wireless receiver or adapter.</summary>
public static class ControllerConnections
{
    public const string Usb = "usb";
    public const string Receiver = "receiver";
    public const string Bluetooth = "bluetooth";
    public const string Unknown = "unknown";
}

public static class WakeTestStates
{
    public const string Idle = "idle";
    public const string Countdown = "countdown";
    public const string Sleeping = "sleeping";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>What "powercfg /lastwake" blamed for the wake after a test.</summary>
public static class WakeTestOutcomes
{
    public const string Controller = "controller";
    public const string OtherDevice = "otherDevice";
    public const string OtherSource = "otherSource";
    public const string NoSource = "noSource";
}

/// <summary>One line inside a card, for example one Wake-on-LAN check. Status is a <see cref="GameReadyCheckStatuses"/> value.</summary>
public sealed record GameReadyCheck(string Id, string Title, string Status, string? Detail, bool CanFix);

/// <summary>A controller or receiver on the Wake from controller card. Kind, Connection, and Wake are the constants above.</summary>
public sealed record GameReadyController(string Id, string Name, string Kind, string Connection, string Wake, string? Note);

/// <param name="State"><see cref="GameReadyStates"/>.</param>
/// <param name="Summary">One line on how the card stands.</param>
/// <param name="Hints">Explanations and manual steps shown under the card.</param>
/// <param name="CanFix">True when the Fix action would change something and may run now.</param>
/// <param name="FixBlockedReason">Why Fix is missing although something could be fixed (for example, no administrator rights).</param>
/// <param name="CanTestWake">True on the controller card when at least one device is armed.</param>
public sealed record GameReadyCard(
    string Id,
    string Title,
    string State,
    string Summary,
    IReadOnlyList<GameReadyCheck> Checks,
    IReadOnlyList<GameReadyController> Controllers,
    IReadOnlyList<string> Hints,
    bool CanFix,
    string? FixBlockedReason,
    bool CanTestWake);

/// <summary>The Test wake action's progress and result.</summary>
/// <param name="State"><see cref="WakeTestStates"/>.</param>
/// <param name="SleepAt">While counting down: when the PC goes to sleep.</param>
/// <param name="ResumedAt">When the PC woke, once done.</param>
/// <param name="Outcome"><see cref="WakeTestOutcomes"/>, once done.</param>
/// <param name="Source">The wake source's name as Windows reported it.</param>
/// <param name="Message">What the result means, or why the test failed.</param>
public sealed record GameReadyWakeTest(
    string State,
    DateTimeOffset? SleepAt,
    DateTimeOffset? ResumedAt,
    string? Outcome,
    string? Source,
    string? Message);

/// <summary>Everything the tray shows in the Game ready section. Sent whole on every change.</summary>
/// <param name="ReadAt">When the PC was last read; null before the first read finishes.</param>
/// <param name="Busy">A <see cref="GameReadyBusy"/> value, or null when idle.</param>
/// <param name="Error">Why the last action was refused or failed, if it was.</param>
public sealed record GameReadyStatus(
    DateTimeOffset? ReadAt,
    string? Busy,
    string? Error,
    IReadOnlyList<GameReadyCard> Cards,
    GameReadyWakeTest WakeTest);

/// <summary>The Game ready status, sent on connect and after every change.</summary>
public sealed record GameReadyStatusMessage(GameReadyStatus Status) : TrayMessage;

/// <summary>Asks for <see cref="GameReadyStatusMessage"/>. The service reads the PC again when its last read is old, or always when Force is set.</summary>
public sealed record GameReadyQueryMessage(bool Force = false) : TrayMessage;

/// <summary>Runs the Fix action of one card (<see cref="GameReadyCardIds.Fixable"/>).</summary>
public sealed record GameReadyFixMessage(string CardId) : TrayMessage;

/// <summary>Puts the PC to sleep after the delay (5 to 300 seconds) and reports what woke it.</summary>
public sealed record GameReadyTestWakeMessage(int DelaySeconds) : TrayMessage;
