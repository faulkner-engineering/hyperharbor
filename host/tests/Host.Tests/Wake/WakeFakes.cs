using System.Net;
using HyperHarbor.Host.Core.Wake;

namespace HyperHarbor.Host.Tests;

/// <summary>Returns a configurable <see cref="WakeEnvironment"/>.</summary>
internal sealed class FakeWakeEnvironment : IWakeEnvironmentReader
{
    public WakeEnvironment Environment { get; set; } = WakeScenarios.ReadyDesktop();

    public Task<WakeEnvironment> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Environment);
}

internal sealed class FakeSleepController : ISleepController
{
    public int SleepCount { get; private set; }

    public void Sleep() => SleepCount++;
}

/// <summary>Environments modeled on real machines.</summary>
internal static class WakeScenarios
{
    public const string EthernetDescription = "Intel(R) Ethernet Connection (10) I219-V";

    /// <summary>A desktop on Ethernet with classic sleep and everything enabled.</summary>
    public static WakeEnvironment ReadyDesktop() => new(
        [
            Ethernet(connected: true, address: "192.168.1.20", wakeOnMagicPacket: true),
        ],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { EthernetDescription },
        new PowerState(S3Supported: true, ModernStandby: false, StandbyConnectivitySupported: false, StandbyConnectivityAc: null, FastStartupEnabled: false));

    /// <summary>
    /// The development laptop as measured on 2026-10-02: Wi-Fi only, Ethernet unplugged with
    /// magic packet disabled, no network device wake-armed, Modern Standby with connectivity off.
    /// </summary>
    public static WakeEnvironment WifiLaptop() => new(
        [
            Ethernet(connected: false, address: "169.254.75.107", wakeOnMagicPacket: false, prefix: 16),
            new NetworkAdapterState(
                "Wi-Fi",
                "Intel(R) Wi-Fi 6 AX201 160MHz",
                "201E88962B07",
                IsPhysical: true,
                IsWired: false,
                IsConnected: true,
                [new Ipv4Assignment(IPAddress.Parse("192.168.0.70"), 24)],
                WakeOnMagicPacket: true),
        ],
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HID Keyboard Device (003)" },
        new PowerState(S3Supported: false, ModernStandby: true, StandbyConnectivitySupported: true, StandbyConnectivityAc: 0, FastStartupEnabled: false));

    public static NetworkAdapterState Ethernet(bool connected, string address, bool? wakeOnMagicPacket, int prefix = 24) => new(
        "Ethernet",
        EthernetDescription,
        "902E1666C5AE",
        IsPhysical: true,
        IsWired: true,
        IsConnected: connected,
        [new Ipv4Assignment(IPAddress.Parse(address), prefix)],
        wakeOnMagicPacket);
}
