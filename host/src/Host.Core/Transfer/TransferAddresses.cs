using HyperHarbor.Host.Core.Wake;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>
/// The receiving host's own addresses, best first, for the sending host to try (TransferOffer.addresses). Only connected
/// adapters with a default route count, since they carry the LAN (the Hyper-V Default Switch's adapter and Tailscale have
/// none; the client adds the addresses it knows after these). Wired before wireless, then the faster link: a laptop with
/// both took a transfer over Wi-Fi at under 68 MB/s, which then dropped, while its 1 Gbps Ethernet sat idle (2026-10-22).
/// Wi-Fi can report a higher link speed than Ethernet (2.4 Gbps against 1 Gbps there), so speed only orders within each.
/// </summary>
public static class TransferAddresses
{
    public static IReadOnlyList<string> Rank(IEnumerable<NetworkAdapterState> adapters) =>
        adapters
            .Where(adapter => adapter.IsConnected && adapter.HasDefaultGateway)
            .OrderBy(adapter => adapter.IsWireless)
            .ThenByDescending(adapter => adapter.LinkSpeed)
            .SelectMany(adapter => adapter.Ipv4Addresses.Where(address => !address.IsLinkLocal))
            .Select(address => address.Address.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
