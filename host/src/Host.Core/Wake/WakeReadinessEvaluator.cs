using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Core.Wake;

/// <summary>
/// Turns a <see cref="WakeEnvironment"/> into readiness checks. Pure, so every branch is unit tested.
/// BIOS or UEFI wake settings cannot be read from Windows; the Test Wake flow covers them.
/// </summary>
public static class WakeReadinessEvaluator
{
    public static WakeReadiness Evaluate(WakeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var wired = WakeInfoBuilder.WiredAdapters(environment).ToList();
        var checks = new List<WakeCheck>
        {
            WiredAdapter(environment, wired),
            MagicPacket(wired),
            AllowWake(environment, wired),
            SleepKeepsNetwork(environment.Power),
            FastStartup(environment.Power),
        };

        return new WakeReadiness(checks.All(check => check.Status != WakeCheckStatus.Fail), checks);
    }

    private static WakeCheck WiredAdapter(WakeEnvironment environment, List<NetworkAdapterState> wired)
    {
        const string title = "Wired network connection";
        var usable = WakeInfoBuilder.Build(environment).Adapters;
        if (usable.Count > 0)
        {
            return new WakeCheck(
                WakeCheckIds.WiredAdapter,
                title,
                WakeCheckStatus.Pass,
                $"Connected through {string.Join(", ", usable.Select(adapter => adapter.Name))}.",
                false);
        }

        var detail = wired.Count == 0
            ? "No wired network adapter was found. Wake-on-LAN almost always requires Ethernet; Wi-Fi adapters rarely support it."
            : $"{string.Join(", ", wired.Select(a => a.Name))} is not connected to the network. Connect an Ethernet cable; Wi-Fi adapters rarely support Wake-on-LAN.";
        return new WakeCheck(WakeCheckIds.WiredAdapter, title, WakeCheckStatus.Fail, detail, false);
    }

    private static WakeCheck MagicPacket(List<NetworkAdapterState> wired)
    {
        const string title = "Network adapter wakes on magic packet";
        if (wired.Count == 0)
        {
            return new WakeCheck(WakeCheckIds.NicWakeOnMagicPacket, title, WakeCheckStatus.Fail, "No wired network adapter was found.", false);
        }

        var disabled = wired.Where(adapter => adapter.WakeOnMagicPacket == false).ToList();
        if (disabled.Count > 0)
        {
            return new WakeCheck(
                WakeCheckIds.NicWakeOnMagicPacket,
                title,
                WakeCheckStatus.Fail,
                $"Wake on Magic Packet is disabled on {Names(disabled)}.",
                true);
        }

        var unknown = wired.Where(adapter => adapter.WakeOnMagicPacket is null).ToList();
        if (unknown.Count == wired.Count)
        {
            return new WakeCheck(
                WakeCheckIds.NicWakeOnMagicPacket,
                title,
                WakeCheckStatus.Warn,
                $"The driver for {Names(unknown)} does not report a Wake on Magic Packet setting. Check the adapter's Advanced properties.",
                false);
        }

        return new WakeCheck(WakeCheckIds.NicWakeOnMagicPacket, title, WakeCheckStatus.Pass, "Wake on Magic Packet is enabled.", false);
    }

    private static WakeCheck AllowWake(WakeEnvironment environment, List<NetworkAdapterState> wired)
    {
        const string title = "Network adapter is allowed to wake the computer";
        if (wired.Count == 0)
        {
            return new WakeCheck(WakeCheckIds.NicAllowWake, title, WakeCheckStatus.Fail, "No wired network adapter was found.", false);
        }

        var notArmed = wired.Where(adapter => !environment.WakeArmedDevices.Contains(adapter.Description)).ToList();
        if (notArmed.Count == 0)
        {
            return new WakeCheck(WakeCheckIds.NicAllowWake, title, WakeCheckStatus.Pass, "Windows allows the adapter to wake the computer.", false);
        }

        // powercfg /deviceenablewake only works on devices Windows lists as wake-programmable.
        var programmable = notArmed.Where(adapter => environment.WakeProgrammableDevices.Contains(adapter.Description)).ToList();
        if (programmable.Count == notArmed.Count)
        {
            return new WakeCheck(
                WakeCheckIds.NicAllowWake,
                title,
                WakeCheckStatus.Fail,
                $"Windows does not allow {Names(notArmed)} to wake the computer.",
                true);
        }

        return new WakeCheck(
            WakeCheckIds.NicAllowWake,
            title,
            WakeCheckStatus.Fail,
            $"Windows does not allow {Names(notArmed)} to wake the computer, and the setting cannot be changed right now. " +
            "This usually means the adapter is disconnected or its driver does not support waking from the current sleep mode. " +
            "Connect the cable, then check the adapter's Power Management tab in Device Manager.",
            false);
    }

    private static WakeCheck SleepKeepsNetwork(PowerState power)
    {
        const string title = "Sleep keeps the network adapter powered";
        if (!power.ModernStandby)
        {
            return power.S3Supported
                ? new WakeCheck(WakeCheckIds.SleepKeepsNetwork, title, WakeCheckStatus.Pass, "The PC uses classic sleep (S3), which supports Wake-on-LAN.", false)
                : new WakeCheck(WakeCheckIds.SleepKeepsNetwork, title, WakeCheckStatus.Warn, "No sleep state was reported. Wake from hibernate or shutdown may still work if the firmware supports it.", false);
        }

        if (!power.StandbyConnectivitySupported)
        {
            return new WakeCheck(
                WakeCheckIds.SleepKeepsNetwork,
                title,
                WakeCheckStatus.Fail,
                "This PC uses Modern Standby without network connectivity, so the adapter is off while asleep. Wake from hibernate may work if the firmware supports it.",
                false);
        }

        if (power.StandbyConnectivityPolicyAc is { } policy)
        {
            return policy == 0
                ? new WakeCheck(
                    WakeCheckIds.SleepKeepsNetwork,
                    title,
                    WakeCheckStatus.Fail,
                    "Group Policy turns the network off while asleep. Change \"Allow network connectivity during connected-standby (plugged in)\" " +
                    "under Computer Configuration > Administrative Templates > System > Power Management > Sleep Settings.",
                    false)
                : new WakeCheck(WakeCheckIds.SleepKeepsNetwork, title, WakeCheckStatus.Pass, "Group Policy keeps the network connected in Modern Standby on AC power.", false);
        }

        return power.StandbyConnectivityAc is null or 0
            ? new WakeCheck(
                WakeCheckIds.SleepKeepsNetwork,
                title,
                WakeCheckStatus.Fail,
                "Modern Standby turns the network off while asleep (connectivity in standby is disabled on AC power).",
                true)
            : new WakeCheck(WakeCheckIds.SleepKeepsNetwork, title, WakeCheckStatus.Pass, "Modern Standby keeps the network connected on AC power.", false);
    }

    private static WakeCheck FastStartup(PowerState power)
    {
        const string title = "Fast startup is off";
        return power.FastStartupEnabled == true
            ? new WakeCheck(
                WakeCheckIds.FastStartupDisabled,
                title,
                WakeCheckStatus.Warn,
                "Fast startup is on. Waking from sleep works, but waking a shut-down PC usually does not.",
                true)
            : new WakeCheck(WakeCheckIds.FastStartupDisabled, title, WakeCheckStatus.Pass, "Fast startup is off.", false);
    }

    private static string Names(IEnumerable<NetworkAdapterState> adapters) =>
        string.Join(", ", adapters.Select(adapter => adapter.Name));
}
