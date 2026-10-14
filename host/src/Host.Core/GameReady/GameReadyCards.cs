using System.Text.Json;
using HyperHarbor.Host.Core.GameReady.Ambient;
using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Core.Tailscale;
using HyperHarbor.Shared.Contracts.Hosts;
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

    public const string AutoSignInRisk =
        "Automatic sign-in means anyone with physical access to this PC reaches the Console desktop. The Console user is a standard account: it cannot open the host's settings, the stored VM passwords, or the HyperHarbor data folder.";

    public const string SteamSignInHint =
        "Sign in to Steam once as the Console user (or have it remember the login) so the launcher opens ready to play.";

    public const string SleepRequirementHint =
        "Setup also turns off \"require sign-in after sleep\". Windows keeps that setting per power plan, not per user, so it applies to every account while this plan is active. It is restored when the Console sign-in is turned off.";

    public const string ChordUnlockHint =
        "Coming later: an option to require a controller button chord before the Console desktop unlocks.";

    /// <summary>The Console sign-in card: a dedicated standard user, signed in automatically, with a launcher.</summary>
    /// <param name="elevated">Whether the service may create accounts and change Winlogon itself.</param>
    public static GameReadyCard ConsoleSignIn(ConsoleSignInReading reading, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var hints = new List<string> { AutoSignInRisk };
        if (reading.Admins.BlocksAdminAutoSignIn)
        {
            var who = string.Join(", ", reading.Admins.MicrosoftAccountAdministrators);
            hints.Add($"The administrator account {who} is a Microsoft account and this PC requires Windows Hello for it. That blocks automatic sign-in for that account, but not for the local Console user.");
        }

        if (reading.PlaintextPasswordPresent)
        {
            hints.Add("A password is stored in plain text in the Winlogon registry key (DefaultPassword). Remove it: HyperHarbor keeps the Console password in the Windows LSA secret store instead.");
        }

        hints.Add(SleepRequirementHint);
        hints.Add(SteamSignInHint);
        hints.Add(ChordUnlockHint);

        var checks = new List<GameReadyCheck>
        {
            Check("userExists", $"Console user \"{reading.UserName}\" exists", reading.UserExists, reading.UserExists ? null : "Created when you set this up."),
            Check("standardUser", "Standard user", reading.UserIsStandard, reading.UserExists && !reading.UserIsStandard ? "The user belongs to a privileged group." : null),
            Check("autoSignIn", "Automatic sign-in set", reading.AutoSignInSet, null),
            Check("launcher", $"{ConsoleSignInLauncherName(reading)} starts at sign-in", reading.LauncherSet, null),
        };

        string state;
        string summary;
        if (!reading.Configured)
        {
            state = elevated ? GameReadyStates.Manual : GameReadyStates.Unavailable;
            summary = "Not set up.";
        }
        else if (reading.Healthy)
        {
            state = GameReadyStates.Ready;
            summary = $"Ready: this PC signs in as {reading.UserName} and opens the launcher.";
        }
        else
        {
            state = GameReadyStates.Attention;
            summary = "Set up, but something changed. Set it up again to repair it.";
        }

        return new GameReadyCard(
            GameReadyCardIds.ConsoleSignIn,
            "Console sign-in",
            state,
            summary,
            reading.Configured ? checks : NoChecks,
            NoControllers,
            hints,
            false,
            elevated ? null : NotElevatedReason,
            false);
    }

    public const string PowerPlanHint =
        "Power modes change the settings of the active Windows power plan in place, so GPU vendor and launcher tweaks in that plan stay. Balanced puts back the values the plan had before.";

    public const string ScheduleHint =
        "The schedule is two Windows scheduled tasks: one puts the host to sleep at the sleep time when no remote session, job, or game is in use (and tries again every 15 minutes until the wake time), the other wakes it at the wake time. Outside the sleep hours, or with the schedule off, the host never sleeps on its own. Wake timers must stay allowed in the plan, and the wake works only on AC power.";

    /// <summary>The Power mode card: Console idle, Balanced, or Sleep when idle.</summary>
    /// <param name="elevated">Whether the service may change the power plan and register tasks itself.</param>
    public static GameReadyCard PowerMode(PowerModeReading reading, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(reading);

        var settings = reading.Settings;
        var hints = new List<string> { PowerPlanHint };
        var checks = new List<GameReadyCheck>();
        if (settings.Mode == PowerModes.ConsoleIdle)
        {
            var expected = reading.Expected;
            var current = reading.Current;
            checks.Add(Check("sleepNever", "Sleep on AC power: never", current?.SleepSeconds == 0, current is null ? null : Describe(current.SleepSeconds)));
            checks.Add(Check("hibernateNever", "Hibernate on AC power: never", current?.HibernateSeconds == 0, current is null ? null : Describe(current.HibernateSeconds)));
            checks.Add(Check("unattendedSleep", "No sleep after an unattended wake", current?.UnattendedSleepSeconds == 0, current is null ? null : Describe(current.UnattendedSleepSeconds)));
            checks.Add(Check("display", $"Display turns off after {settings.DisplayMinutes} minutes", current is not null && expected is not null && current.DisplaySeconds == expected.DisplaySeconds, current is null ? null : Describe(current.DisplaySeconds)));
            if (settings.ScheduleEnabled)
            {
                checks.Add(Check("wakeTimers", "Wake timers allowed in the plan", current?.WakeTimers == 1, null));
                checks.Add(Check("scheduleTasks", $"Sleep at {settings.SleepTime}, wake at {settings.WakeTime}", reading.ScheduleRegistered, "The scheduled tasks are missing."));
            }

            hints.Add(ScheduleHint);
            hints.Add("A controller press should turn the display back on. If it does not, the tray wakes the display itself when it sees controller input while the display is off.");
        }

        string state;
        string summary;
        if (reading.Current is null)
        {
            state = GameReadyStates.Unavailable;
            summary = "Windows did not report the power plan's settings.";
        }
        else if (settings.Mode == PowerModes.Balanced)
        {
            state = reading.Applied ? GameReadyStates.Ready : GameReadyStates.Attention;
            summary = reading.Applied ? "Balanced: this PC follows its own power plan." : "Balanced, but the earlier plan values are still changed. Apply Balanced again.";
        }
        else if (!reading.Applied || !reading.ScheduleInPlace)
        {
            state = GameReadyStates.Attention;
            summary = "The power plan no longer matches this mode. Apply it again.";
        }
        else if (settings.Mode == PowerModes.ConsoleIdle)
        {
            state = GameReadyStates.Ready;
            summary = settings.ScheduleEnabled
                ? $"Console idle: always ready, display off after {settings.DisplayMinutes} minutes, asleep {settings.SleepTime} to {settings.WakeTime}."
                : $"Console idle: always ready, display off after {settings.DisplayMinutes} minutes.";
        }
        else
        {
            state = GameReadyStates.Ready;
            summary = "Sleep when idle: display off after 5 minutes, sleep after 15.";
        }

        return new GameReadyCard(
            GameReadyCardIds.PowerMode,
            "Power mode",
            state,
            summary,
            checks,
            NoControllers,
            hints,
            false,
            elevated ? null : NotElevatedReason,
            false);
    }

    public const string AmbientHint =
        "The ambient screen is a Windows screensaver: Windows starts it after the idle time and ends it on keyboard or mouse input. The tray also ends it when a controller changes. Windows does not start a screensaver while a full-screen game or a presentation runs.";

    public const string SoftLockHint =
        "The soft lock only asks for the chord on a controller. It is not a security boundary: keyboard and mouse input (including what Steam Input sends for a desktop layout) still ends the screen, and Windows itself is not locked.";

    public const string CecHint =
        "HDMI-CEC standby on display off is not built yet; HyperHarbor only reports whether an adapter is present.";

    /// <summary>The Ambient screen card: a black clock screen between activity and the display turning off.</summary>
    /// <param name="power">The saved power mode; the screen belongs to Console idle.</param>
    /// <param name="reported">The screensaver as the tray found it on the desktop; null until the tray reports.</param>
    public static GameReadyCard AmbientScreen(AmbientReading reading, PowerModeSettings power, AmbientScreensaverState? reported, bool elevated)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(power);

        var settings = reading.Settings;
        var hints = new List<string> { AmbientHint };
        var checks = new List<GameReadyCheck>();
        string state;
        string summary;
        if (!reading.Supported)
        {
            state = GameReadyStates.Unavailable;
            summary = reading.UnsupportedReason ?? "The ambient screen is not available here.";
        }
        else if (!settings.Enabled)
        {
            state = GameReadyStates.Ready;
            summary = "Off: the display turns off at the power plan's timeout.";
        }
        else
        {
            var displaySeconds = power.DisplayMinutes * 60;
            var modeOk = power.Mode == PowerModes.ConsoleIdle;
            checks.Add(new GameReadyCheck("ambientMode", "Console idle power mode", modeOk ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Fail, modeOk ? null : "Apply Console idle first.", false));
            checks.Add(Check("ambientFile", "Screensaver file installed", reading.ScreensaverPath is not null, "It is installed when you apply."));

            var applied = reported is { Active: true } && reading.ScreensaverPath is not null
                && string.Equals(reported.Path, reading.ScreensaverPath, StringComparison.OrdinalIgnoreCase)
                && reported.TimeoutSeconds == settings.IdleMinutes * 60;
            checks.Add(Check(
                "ambientSet",
                $"Windows starts it after {settings.IdleMinutes} minutes",
                applied,
                reported is null ? "The tray has not reported yet." : reported.PolicyLocked ? "Group Policy sets the screensaver." : "The screensaver setting differs; the tray sets it again shortly."));

            // The check reads the timeout Windows actually has, not only what was saved.
            var actual = reported is { Active: true } ? reported.TimeoutSeconds : 0;
            var order = AmbientTimeouts.OrderProblem(actual > 0 ? actual : settings.IdleMinutes * 60, displaySeconds);
            checks.Add(Check("ambientOrder", $"Screen starts before the display turns off ({power.DisplayMinutes} min)", order is null, order));

            state = modeOk && applied && order is null && reading.ScreensaverPath is not null ? GameReadyStates.Ready : GameReadyStates.Attention;
            summary = state == GameReadyStates.Ready
                ? $"On: a clock screen after {settings.IdleMinutes} minutes, display off after {power.DisplayMinutes}."
                : "The ambient screen is not fully in place yet.";
            if (settings.SoftLock)
            {
                hints.Add($"Soft lock is on: hold {AmbientChord.Describe(settings.Chord)} on a controller to dismiss.");
                hints.Add(SoftLockHint);
            }
        }

        checks.Add(new GameReadyCheck(
            "ambientCec",
            "HDMI-CEC adapter",
            GameReadyCheckStatuses.Pass,
            reading.CecDevice is { } cec ? $"{cec} found. Standby over CEC is not built yet." : "None found. Standby over CEC is not built yet.",
            false));
        hints.Add(CecHint);

        return new GameReadyCard(
            GameReadyCardIds.AmbientScreen,
            "Ambient screen",
            state,
            summary,
            checks,
            NoControllers,
            hints,
            false,
            elevated ? null : NotElevatedReason,
            false);
    }

    private static string Describe(int seconds) => seconds == 0 ? "Never." : $"{seconds / 60} minutes.";

    private static string ConsoleSignInLauncherName(ConsoleSignInReading reading) =>
        reading.Launchers.FirstOrDefault(launcher => launcher.Id == reading.Launcher)?.Name ?? "The launcher";

    private static GameReadyCheck Check(string id, string title, bool pass, string? detail) =>
        new(id, title, pass ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Fail, pass ? null : detail, false);

    /// <summary>The cards that are in place but not built yet, so the section keeps its final shape.</summary>
    public static IReadOnlyList<GameReadyCard> ComingSoon() =>
    [
        Soon(GameReadyCardIds.BootToLauncher, "Boot to launcher", "Start the game launcher straight after sign-in, without the desktop."),
        Soon(GameReadyCardIds.SteamDesktopLayout, "Steam desktop layout", "A Steam controller layout for using the desktop with a gamepad."),
        Soon(GameReadyCardIds.LeanHost, "Lean host", "Trim the PC for gaming. This card will summarize the Lean host page."),
    ];

    /// <summary>
    /// The Game-aware card. The page shows the live state and switch from the game-aware settings, which the tray already
    /// holds; the card only keeps its place in the section.
    /// </summary>
    public static GameReadyCard GameAware() =>
        new(GameReadyCardIds.GameAwareMode, "Game-aware mode", GameReadyStates.Ready,
            "Pause or save running VMs while a game is in the foreground, and resume them afterwards.", NoChecks, NoControllers, NoHints, false, null, false);

    /// <summary>
    /// The Tailscale card. Tailscale is optional, so a PC without it is unavailable rather than needing attention. The tag only
    /// decides which hosts a client tries first on the tailnet; without it the host is still found, a little later.
    /// </summary>
    public static GameReadyCard Tailscale(HostTailscale info)
    {
        ArgumentNullException.ThrowIfNull(info);

        var installed = info.State != TailscaleHostState.NotInstalled;
        var signedIn = info.State is TailscaleHostState.Running or TailscaleHostState.Stopped;
        var running = info.State == TailscaleHostState.Running;
        var checks = new List<GameReadyCheck>
        {
            new("installed", "Tailscale is installed", installed ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Fail,
                installed ? null : "Install Tailscale from tailscale.com/download, then check again.", false),
        };

        if (info.State == TailscaleHostState.Unreachable)
        {
            checks.Add(new GameReadyCheck("answering", "Tailscale answers", GameReadyCheckStatuses.Warn,
                info.Error ?? "Tailscale did not answer. Check that its service is running, or set the local API port below.", false));
        }
        else if (installed)
        {
            checks.Add(new GameReadyCheck("signedIn", "Signed in", signedIn ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Fail,
                signedIn ? null : "Sign in from the Tailscale icon in the notification area.", false));
            checks.Add(new GameReadyCheck("connected", "Connected to the tailnet", running ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Fail,
                running ? null : "Tailscale is turned off. Turn it on from its notification-area icon.", false));
        }

        if (running)
        {
            checks.Add(new GameReadyCheck("address", "Address", GameReadyCheckStatuses.Pass,
                string.Join(", ", new[] { info.Ipv4, info.DnsName }.Where(part => !string.IsNullOrEmpty(part))), false));
            checks.Add(new GameReadyCheck("tag", $"Tagged {info.Tag}", info.Tagged ? GameReadyCheckStatuses.Pass : GameReadyCheckStatuses.Warn,
                info.Tagged ? null : "Not tagged. Clients still find this PC, but look at tagged PCs first.", false));
        }

        var hints = new List<string>();
        if (running && !info.Tagged)
        {
            hints.Add($"To tag this PC, run the command below in a terminal. Your tailnet policy must list {info.Tag} under tagOwners first. If Tailscale asks you to repeat your other options, add them to the command.");
        }

        if (running)
        {
            hints.Add("Remote Desktop to your VMs still goes through this host, so the VMs need no Tailscale. To check the connection, open this PC in a HyperHarbor client and choose Host > Tailscale > Test reachability.");
        }

        var (state, summary) = info.State switch
        {
            TailscaleHostState.NotInstalled => (GameReadyStates.Unavailable, "Tailscale is not installed. It is optional: it lets clients reach this PC away from home."),
            TailscaleHostState.Running when info.Tagged => (GameReadyStates.Ready, $"Ready: reachable at {info.DnsName ?? info.Ipv4} on your tailnet."),
            TailscaleHostState.Running => (GameReadyStates.Attention, "Connected, but this PC does not have the HyperHarbor tag."),
            TailscaleHostState.Unreachable => (GameReadyStates.Attention, "Tailscale is installed but did not answer."),
            TailscaleHostState.NeedsLogin => (GameReadyStates.Attention, "Tailscale is installed. Sign in to use it."),
            TailscaleHostState.Stopped => (GameReadyStates.Attention, "Tailscale is turned off."),
            _ => (GameReadyStates.Attention, "Tailscale is starting or waiting for approval."),
        };

        return new GameReadyCard(GameReadyCardIds.Tailscale, "Tailscale", state, summary, checks, NoControllers, hints, false, null, false);
    }

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
