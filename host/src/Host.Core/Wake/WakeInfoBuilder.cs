using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Core.Wake;

/// <summary>
/// Selects the adapters a client should send magic packets to.
/// </summary>
public static class WakeInfoBuilder
{
    /// <summary>
    /// Wired physical adapters with a usable IPv4 address. When the adapter is bound to a Hyper-V
    /// external switch its address lives on the vEthernet adapter that shares its MAC address,
    /// but the magic packet must still carry the physical adapter's MAC.
    /// </summary>
    public static WakeInfo Build(WakeEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var adapters = new List<WakeAdapter>();
        foreach (var adapter in WiredAdapters(environment))
        {
            var assignment = UsableAddress(adapter)
                ?? environment.Adapters
                    .Where(other => !other.IsPhysical && other.MacAddress == adapter.MacAddress)
                    .Select(UsableAddress)
                    .FirstOrDefault(found => found is not null);
            if (assignment is null)
            {
                continue;
            }

            adapters.Add(new WakeAdapter(
                adapter.Name,
                FormatMac(adapter.MacAddress),
                assignment.Address.ToString(),
                assignment.Broadcast.ToString()));
        }

        return new WakeInfo(adapters);
    }

    internal static IEnumerable<NetworkAdapterState> WiredAdapters(WakeEnvironment environment) =>
        environment.Adapters.Where(adapter => adapter.IsPhysical && adapter.IsWired);

    private static Ipv4Assignment? UsableAddress(NetworkAdapterState adapter) =>
        adapter.Ipv4Addresses.FirstOrDefault(assignment => !assignment.IsLinkLocal);

    /// <summary>AA-BB-CC-DD-EE-FF, as specified by WakeAdapter.macAddress.</summary>
    public static string FormatMac(string mac) =>
        string.Join('-', Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2)));
}
