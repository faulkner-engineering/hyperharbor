namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>Remote Desktop to the host itself, for maintenance. Schema: HostRemoteDesktop.</summary>
/// <param name="Supported">False on editions that cannot accept Remote Desktop connections (Windows Home).</param>
/// <param name="Enabled">Remote Desktop connections are allowed (fDenyTSConnections is 0).</param>
/// <param name="Port">The TCP port Remote Desktop listens on.</param>
/// <param name="FirewallOpen">The Windows Firewall "Remote Desktop" rule group is enabled.</param>
/// <param name="Edition">The Windows edition, for display, for example "Windows 11 Pro".</param>
public sealed record HostRemoteDesktop(bool Supported, bool Enabled, int Port, bool FirewallOpen, string Edition);
