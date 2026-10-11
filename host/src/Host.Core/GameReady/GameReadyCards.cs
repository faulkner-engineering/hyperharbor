using System.Text.Json;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Core.GameReady;

/// <summary>
/// Builds the cards of the Game ready section from what was read. Pure. Wake-on-LAN is the readiness report the
/// clients also get (<see cref="WakeReadinessEvaluator"/>); the other cards are new.
/// </summary>
public static class GameReadyCards
{
    public const string NotElevatedReason = "Changing this needs administrator rights. Run the installed HyperHarbor service.";

    private static readonly IReadOnlyList<GameReadyCheck> NoChecks = [];
    private static readonly IReadOnlyList<GameReadyController> NoControllers = [];
    private static readonly IReadOnlyList<string> NoHints = [];

    /// <summary>The Wake-on-LAN card. Fast startup has a card of its own, so its check is left out here.</summary>
    public static GameReadyCard WakeOnLan(WakeReadiness readiness)
    {
        ArgumentNullException.ThrowIfNull(readiness);

        var checks = readiness.Checks
            .Where(check => check.Id != WakeCheckIds.FastStartupDisabled)
            .Select(check => new GameReadyCheck(check.Id, check.Title, StatusOf(check.Status), check.Detail, check.AutoFixAvailable))
            .ToList();
        var attention = checks.Count(check => check.Status != GameReadyCheckStatuses.Pass);
        var canFix = checks.Any(check => check.CanFix);

        return new GameReadyCard(
            GameReadyCardIds.WakeOnLan,
            "Wake-on-LAN",
            attention == 0 ? GameReadyStates.Ready : GameReadyStates.Attention,
            attention == 0
                ? "Ready: a magic packet can wake this PC."
                : attention == 1 ? "1 check needs attention." : $"{attention} checks need attention.",
            checks,
            NoControllers,
            ["Firmware (BIOS or UEFI) wake settings cannot be read from Windows. Use Test Wake in a HyperHarbor client to prove that a magic packet wakes this PC."],
            canFix,
            null,
            false);
    }

    /// <summary>The Wake from controller card.</summary>
    /// <param name="elevated">Whether the service may run powercfg changes itself.</param>
    public static GameReadyCard WakeFromController(IReadOnlyList<GameController> controllers, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(controllers);

        var shown = controllers
            .Select(controller => new GameReadyController(
                controller.Id,
                controller.Name,
                WireName(controller.Kind),
                WireName(controller.Connection),
                WireName(controller.Wake),
                controller.Note))
            .ToList();

        var armed = controllers.Count(controller => controller.Wake == ControllerWake.Armed);
        var canArm = controllers.Count(controller => controller.Wake == ControllerWake.CanArm);
        var hints = new List<string>();
        if (controllers.Any(controller => controller.Connection == ControllerConnection.Bluetooth))
        {
            hints.Add(ControllerWakeEvaluator.BluetoothNote);
        }

        if (controllers.Any(controller => controller.Kind == ControllerKind.Receiver && controller.Wake == ControllerWake.CannotWake))
        {
            hints.Add(ControllerWakeEvaluator.ReceiverNote);
        }

        string state;
        string summary;
        if (controllers.Count == 0)
        {
            state = GameReadyStates.Unavailable;
            summary = "No game controller or receiver was found.";
            hints.Add("Connect a controller with its USB cable, or plug in its wireless receiver, then check again.");
        }
        else if (canArm > 0)
        {
            state = GameReadyStates.Attention;
            summary = canArm == 1 ? "1 device can wake the PC once it is armed." : $"{canArm} devices can wake the PC once they are armed.";
        }
        else if (armed > 0)
        {
            state = GameReadyStates.Ready;
            summary = armed == 1 ? "Ready: 1 device can wake the PC." : $"Ready: {armed} devices can wake the PC.";
        }
        else
        {
            state = GameReadyStates.Attention;
            summary = "None of the connected devices can wake the PC.";
        }

        if (armed > 0 || canArm > 0)
        {
            hints.Add("Test wake puts this PC to sleep, then reports what woke it. Press a button on the controller to wake it.");
        }

        return new GameReadyCard(
            GameReadyCardIds.ControllerWake,
            "Wake from controller",
            state,
            summary,
            NoChecks,
            shown,
            hints,
            canArm > 0 && elevated,
            canArm > 0 && !elevated ? NotElevatedReason : null,
            armed > 0);
    }

    public static GameReadyCard FastStartup(PowerState power)
    {
        ArgumentNullException.ThrowIfNull(power);

        const string hint = "Fast startup keeps the USB ports and network adapter unpowered after a shutdown, so a controller or magic packet usually cannot start the PC from off. Waking from sleep is not affected.";
        return power.FastStartupEnabled == true
            ? new GameReadyCard(GameReadyCardIds.FastStartup, "Fast startup", GameReadyStates.Attention, "On: waking a shut-down PC usually does not work.", NoChecks, NoControllers, [hint], true, null, false)
            : new GameReadyCard(
                GameReadyCardIds.FastStartup,
                "Fast startup",
                power.FastStartupEnabled == false ? GameReadyStates.Ready : GameReadyStates.Unavailable,
                power.FastStartupEnabled == false ? "Off." : "Windows does not report this setting on this PC.",
                NoChecks,
                NoControllers,
                [hint],
                false,
                null,
                false);
    }

    /// <param name="enabledOnAc">Null when the setting could not be read.</param>
    public static GameReadyCard UsbSelectiveSuspend(bool? enabledOnAc, bool elevated)
    {
        const string hint = "With USB selective suspend on, Windows can cut power to idle USB ports, which can stop a controller or receiver from waking the PC.";
        return enabledOnAc switch
        {
            true => new GameReadyCard(
                GameReadyCardIds.UsbSelectiveSuspend,
                "USB selective suspend",
                GameReadyStates.Attention,
                "On: Windows can power down idle USB ports.",
                NoChecks,
                NoControllers,
                [hint],
                elevated,
                elevated ? null : NotElevatedReason,
                false),
            false => new GameReadyCard(GameReadyCardIds.UsbSelectiveSuspend, "USB selective suspend", GameReadyStates.Ready, "Off on AC power.", NoChecks, NoControllers, [hint], false, null, false),
            _ => new GameReadyCard(GameReadyCardIds.UsbSelectiveSuspend, "USB selective suspend", GameReadyStates.Unavailable, "The setting could not be read.", NoChecks, NoControllers, [hint], false, null, false),
        };
    }

    /// <summary>A manual step: firmware settings cannot be read from Windows, so this card only explains.</summary>
    public static GameReadyCard UsbPowerInSleep() => new(
        GameReadyCardIds.UsbPowerInSleep,
        "USB power in sleep (BIOS)",
        GameReadyStates.Manual,
        "Check this in the BIOS or UEFI setup. Windows cannot read it.",
        NoChecks,
        NoControllers,
        [
            "Many boards cut power to USB ports while the PC sleeps, which no Windows setting can undo. Look for an option named like USB Wake Support, Power On By USB, Resume By USB, USB Standby Power, or USB Power in Sleep, and enable it.",
            "Turn ErP or EuP Ready off if the board has it: it removes standby power from the ports.",
            "Some boards power only certain ports in sleep, often the ones marked on the back panel. Plug the receiver into one of those.",
        ],
        false,
        null,
        false);

    /// <summary>The cards that are in place but not built yet, so the section keeps its final shape.</summary>
    public static IReadOnlyList<GameReadyCard> ComingSoon() =>
    [
        Soon(GameReadyCardIds.SleepPolicy, "Sleep policy", "Sleep and display timeouts that suit a PC people connect to remotely."),
        Soon(GameReadyCardIds.BootToLauncher, "Boot to launcher", "Start the game launcher straight after sign-in, without the desktop."),
        Soon(GameReadyCardIds.SteamDesktopLayout, "Steam desktop layout", "A Steam controller layout for using the desktop with a gamepad."),
        Soon(GameReadyCardIds.LeanHost, "Lean host", "Trim the PC for gaming. This card will summarize the Lean host page."),
        Soon(GameReadyCardIds.GameAwareMode, "Game-aware mode", "Hold back updates and background work while a game runs."),
    ];

    private static GameReadyCard Soon(string id, string title, string summary) =>
        new(id, title, GameReadyStates.ComingSoon, summary, NoChecks, NoControllers, NoHints, false, null, false);

    private static string StatusOf(WakeCheckStatus status) => status switch
    {
        WakeCheckStatus.Pass => GameReadyCheckStatuses.Pass,
        WakeCheckStatus.Warn => GameReadyCheckStatuses.Warn,
        _ => GameReadyCheckStatuses.Fail,
    };

    private static string WireName<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}
