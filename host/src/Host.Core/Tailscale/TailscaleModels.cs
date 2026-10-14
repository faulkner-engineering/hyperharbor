using System.Net;
using System.Net.Sockets;

namespace HyperHarbor.Host.Core.Tailscale;

/// <summary>How the installed Tailscale client reports its own state (the BackendState of its status).</summary>
public enum TailscaleState
{
    /// <summary>Neither the local API nor the command line answered, and no client was found.</summary>
    NotInstalled,

    /// <summary>A client is installed but did not answer.</summary>
    Unreachable,

    /// <summary>Signed out, or the login expired.</summary>
    NeedsLogin,

    /// <summary>Signed in with Tailscale turned off.</summary>
    Stopped,

    /// <summary>Connected to the tailnet.</summary>
    Running,

    /// <summary>Any other state, for example starting up or waiting for machine authorization.</summary>
    Other,
}

/// <summary>The path to a peer, as Tailscale reports it. Idle peers report neither.</summary>
public enum TailscalePath
{
    /// <summary>No traffic has chosen a path yet, or the peer is offline.</summary>
    Unknown,

    /// <summary>Packets go straight to the peer (a CurAddr is set).</summary>
    Direct,

    /// <summary>Packets go through a DERP relay server.</summary>
    Relayed,
}

/// <param name="NodeId">The stable node ID (for example "nABC123CNTRL"). It stays when the node key rotates.</param>
/// <param name="Addresses">Tailscale IP addresses without a prefix length, IPv4 first.</param>
/// <param name="DnsName">The MagicDNS name without the trailing dot.</param>
public sealed record TailscaleNode(string NodeId, string HostName, string DnsName, IReadOnlyList<string> Addresses, IReadOnlyList<string> Tags);

/// <param name="Relay">The DERP region code while <paramref name="Path"/> is relayed, otherwise null.</param>
public sealed record TailscalePeer(TailscaleNode Node, bool Online, TailscalePath Path, string? Relay)
{
    public string? Ipv4 => TailscaleAddress.FirstIpv4(Node.Addresses);
}

/// <param name="Self">This node. Null unless the client is running or has a node.</param>
/// <param name="MagicDnsSuffix">For example "tail1234.ts.net". Null when MagicDNS is off.</param>
/// <param name="Error">Why the status is not available, when it is not.</param>
public sealed record TailscaleStatus(
    TailscaleState State,
    TailscaleNode? Self,
    string? MagicDnsSuffix,
    IReadOnlyList<TailscalePeer> Peers,
    string? Error)
{
    public static TailscaleStatus Missing(TailscaleState state, string? error = null) => new(state, null, null, [], error);

    public bool IsRunning => State == TailscaleState.Running;
}

/// <summary>The node behind a Tailscale address, as the local client resolves it.</summary>
public sealed record TailscaleWhoIs(string NodeId, string Name);

/// <summary>Tailscale's address ranges: 100.64.0.0/10 (CGNAT) and fd7a:115c:a1e0::/48.</summary>
public static class TailscaleAddress
{
    public static bool IsTailnet(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] == 100 && (bytes[1] & 0xC0) == 64,
            AddressFamily.InterNetworkV6 => bytes[0] == 0xFD && bytes[1] == 0x7A && bytes[2] == 0x11 && bytes[3] == 0x5C && bytes[4] == 0xA1 && bytes[5] == 0xE0,
            _ => false,
        };
    }

    public static bool IsTailnet(string address) => IPAddress.TryParse(address, out var parsed) && IsTailnet(parsed);

    public static string? FirstIpv4(IEnumerable<string> addresses) =>
        addresses.FirstOrDefault(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
}
