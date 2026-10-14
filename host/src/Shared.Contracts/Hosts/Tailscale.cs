using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>What the host's local Tailscale client reports. Schema: TailscaleState.</summary>
public enum TailscaleHostState
{
    NotInstalled,
    Unreachable,
    NeedsLogin,
    Stopped,
    Running,
    Other,
}

/// <summary>Where the host finds its Tailscale client. Schema: TailscaleConfig.</summary>
/// <param name="LocalApiPort">The loopback port of the client's local API (41112 by default).</param>
/// <param name="ControlUrl">A custom control server (Headscale) used in the suggested "tailscale up" command; null for Tailscale's own.</param>
public sealed record TailscaleConfig([property: JsonRequired] int LocalApiPort, string? ControlUrl);

/// <summary>The host's Tailscale state for the setup cards. Schema: HostTailscale.</summary>
/// <param name="Ipv4">The host's Tailscale IPv4 address while Tailscale runs.</param>
/// <param name="DnsName">The host's MagicDNS name without the trailing dot.</param>
/// <param name="Tagged">The host carries the tag in <paramref name="Tag"/>, so clients prefer it when probing the tailnet.</param>
/// <param name="UpCommand">The command that gives the host the tag. It is a suggestion; the host never runs it.</param>
public sealed record HostTailscale(
    [property: JsonRequired] TailscaleHostState State,
    string? Error,
    string? Ipv4,
    string? DnsName,
    [property: JsonRequired] bool Tagged,
    [property: JsonRequired] string Tag,
    [property: JsonRequired] string UpCommand,
    [property: JsonRequired] TailscaleConfig Config);
