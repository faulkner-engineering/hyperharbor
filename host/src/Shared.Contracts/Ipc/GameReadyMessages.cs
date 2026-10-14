namespace HyperHarbor.Shared.Contracts.Ipc;

/// <summary>The cards of the tray's Game ready section. Stable: the page keys its actions on them.</summary>
public static class GameReadyCardIds
{
    public const string WakeOnLan = "wakeOnLan";
    public const string ControllerWake = "controllerWake";
    public const string FastStartup = "fastStartup";
    public const string UsbSelectiveSuspend = "usbSelectiveSuspend";
    public const string UsbPowerInSleep = "usbPowerInSleep";
    public const string ConsoleSignIn = "consoleSignIn";
    public const string PowerMode = "powerMode";
    public const string AmbientScreen = "ambientScreen";
    public const string BootToLauncher = "bootToLauncher";
    public const string SteamDesktopLayout = "steamDesktopLayout";
    public const string LeanHost = "leanHost";
    public const string GameAwareMode = "gameAwareMode";
    public const string Tailscale = "tailscale";

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

    /// <summary>The Console sign-in is being set up or turned off.</summary>
    public const string ConsoleSignIn = "consoleSignIn";

    /// <summary>A power mode is being applied.</summary>
    public const string PowerMode = "powerMode";

    /// <summary>The Ambient screen choices are being saved.</summary>
    public const string Ambient = "ambient";
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

/// <summary>A launcher the Console user can start at sign-in. Unavailable launchers say why.</summary>
public sealed record ConsoleSignInLauncher(string Id, string Name, bool Available, string? Reason);

/// <summary>
/// What the Console sign-in card needs to offer its setup form: the user name and launcher to propose, whether the
/// sign-in is set up, and the launchers that can be chosen. The password is never part of it.
/// </summary>
public sealed record ConsoleSignInInfo(
    string UserName,
    string Launcher,
    bool Configured,
    IReadOnlyList<ConsoleSignInLauncher> Launchers);

/// <summary>The cost model of the Power mode card. The estimate is a model, not a measurement.</summary>
public sealed record PowerModeEstimate(
    double Watts,
    double AlwaysReadyKwh,
    double AlwaysReadyMonthlyCost,
    double ScheduledKwh,
    double ScheduledMonthlyCost,
    double SleepWatts);

/// <summary>
/// What the Power mode card needs: the saved choice, whether the active plan holds it, the cost model, and the host's
/// measured load. The load is null until measured or when Windows does not report it (no GPU counters).
/// </summary>
public sealed record PowerModeInfo(
    string Mode,
    int DisplayMinutes,
    bool ScheduleEnabled,
    string SleepTime,
    string WakeTime,
    double Watts,
    double RatePerKwh,
    bool Applied,
    PowerModeEstimate Estimate,
    double? CpuPercent,
    double? GpuPercent);

/// <summary>Everything the tray shows in the Game ready section. Sent whole on every change.</summary>
/// <param name="ReadAt">When the PC was last read; null before the first read finishes.</param>
/// <param name="Busy">A <see cref="GameReadyBusy"/> value, or null when idle.</param>
/// <param name="Error">Why the last action was refused or failed, if it was.</param>
public sealed record GameReadyStatus(
    DateTimeOffset? ReadAt,
    string? Busy,
    string? Error,
    IReadOnlyList<GameReadyCard> Cards,
    GameReadyWakeTest WakeTest,
    ConsoleSignInInfo? ConsoleSignIn = null,
    PowerModeInfo? PowerMode = null,
    AmbientInfo? Ambient = null,
    Hosts.HostTailscale? Tailscale = null);

/// <summary>The Game ready status, sent on connect and after every change.</summary>
public sealed record GameReadyStatusMessage(GameReadyStatus Status) : TrayMessage;

/// <summary>Asks for <see cref="GameReadyStatusMessage"/>. The service reads the PC again when its last read is old, or always when Force is set.</summary>
public sealed record GameReadyQueryMessage(bool Force = false) : TrayMessage;

/// <summary>Runs the Fix action of one card (<see cref="GameReadyCardIds.Fixable"/>).</summary>
public sealed record GameReadyFixMessage(string CardId) : TrayMessage;

/// <summary>Puts the PC to sleep after the delay (5 to 300 seconds) and reports what woke it.</summary>
public sealed record GameReadyTestWakeMessage(int DelaySeconds) : TrayMessage;

/// <summary>
/// Sets up the Console sign-in: a standard local user that Windows signs in automatically, with a launcher. Needs the
/// installed service (administrator rights). The password is generated on the host and never sent anywhere.
/// </summary>
public sealed record GameReadyConsoleSignInEnableMessage(string UserName, string Launcher) : TrayMessage;

/// <summary>Turns the Console sign-in off. DeleteUser also deletes the account it created, with its profile.</summary>
public sealed record GameReadyConsoleSignInDisableMessage(bool DeleteUser) : TrayMessage;

/// <summary>Applies a power mode (consoleIdle, balanced, or sleepWhenIdle) and the choices that go with it.</summary>
public sealed record GameReadyPowerModeApplyMessage(
    string Mode,
    int DisplayMinutes,
    bool ScheduleEnabled,
    string SleepTime,
    string WakeTime,
    double Watts,
    double RatePerKwh) : TrayMessage;

/// <summary>Saves where the host finds its Tailscale client: the local API port, and a Headscale control server for the suggested command. The tray pipe admits only administrators, so no elevation token is needed.</summary>
public sealed record GameReadyTailscaleSettingsMessage(int LocalApiPort, string? ControlUrl) : TrayMessage;

/// <summary>Puts the host to sleep after a short delay. Asked for by a person at the host, so nothing gates it.</summary>
public sealed record GameReadySleepNowMessage : TrayMessage;

/// <summary>From the scheduled sleep task: sleep now if nothing is in use.</summary>
public sealed record ScheduledSleepRequestMessage : TrayMessage;

/// <summary>The answer: Accepted when the host is going to sleep, else why not. Final means the task need not try again.</summary>
public sealed record ScheduledSleepResultMessage(bool Accepted, bool Final, string? Reason) : TrayMessage;
