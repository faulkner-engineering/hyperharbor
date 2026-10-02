using System.Net;

namespace HyperHarbor.Host.Core.Wake;

/// <summary>
/// Everything the Wake-on-LAN checks need, read in one pass. Evaluated by
/// <see cref="WakeReadinessEvaluator"/> and <see cref="WakeInfoBuilder"/>.
/// </summary>
/// <param name="WakeArmedDevices">Devices currently allowed to wake the computer (powercfg wake_armed).</param>
/// <param name="WakeProgrammableDevices">Devices whose wake permission can be changed (powercfg wake_programmable).</param>
public sealed record WakeEnvironment(
    IReadOnlyList<NetworkAdapterState> Adapters,
    IReadOnlySet<string> WakeArmedDevices,
    IReadOnlySet<string> WakeProgrammableDevices,
    PowerState Power);

/// <param name="Name">Interface alias, for example "Ethernet".</param>
/// <param name="Description">Driver description, which powercfg uses as the device name.</param>
/// <param name="MacAddress">Permanent address, 12 uppercase hex digits.</param>
/// <param name="IsPhysical">A hardware adapter (not Hyper-V vEthernet, VPN, or loopback).</param>
/// <param name="IsWired">IEEE 802.3 media.</param>
/// <param name="WakeOnMagicPacket">The *WakeOnMagicPacket driver keyword, or null when the driver does not expose it.</param>
public sealed record NetworkAdapterState(
    string Name,
    string Description,
    string MacAddress,
    bool IsPhysical,
    bool IsWired,
    bool IsConnected,
    IReadOnlyList<Ipv4Assignment> Ipv4Addresses,
    bool? WakeOnMagicPacket);

public sealed record Ipv4Assignment(IPAddress Address, int PrefixLength)
{
    public IPAddress Broadcast
    {
        get
        {
            var address = BitConverter.ToUInt32(Address.GetAddressBytes().Reverse().ToArray(), 0);
            var mask = PrefixLength == 0 ? 0u : uint.MaxValue << (32 - PrefixLength);
            var broadcast = address | ~mask;
            return new IPAddress(BitConverter.GetBytes(broadcast).Reverse().ToArray());
        }
    }

    /// <summary>Link-local (169.254.0.0/16) addresses mean DHCP failed or no cable is connected.</summary>
    public bool IsLinkLocal => Address.GetAddressBytes() is [169, 254, ..];
}

/// <param name="S3Supported">Classic sleep. Firmware support; disabled when Modern Standby is active.</param>
/// <param name="ModernStandby">S0 low-power idle (AoAc).</param>
/// <param name="StandbyConnectivitySupported">The platform can keep the network up in Modern Standby.</param>
/// <param name="StandbyConnectivityAc">Power policy CONNECTIVITYINSTANDBY on AC: 0 disabled, 1 enabled, 2 managed by Windows.</param>
/// <param name="FastStartupEnabled">HiberbootEnabled. Only affects waking from shutdown.</param>
/// <param name="StandbyConnectivityPolicyAc">A Group Policy value that overrides StandbyConnectivityAc, when set.</param>
public sealed record PowerState(
    bool S3Supported,
    bool ModernStandby,
    bool StandbyConnectivitySupported,
    uint? StandbyConnectivityAc,
    bool? FastStartupEnabled,
    uint? StandbyConnectivityPolicyAc = null);
