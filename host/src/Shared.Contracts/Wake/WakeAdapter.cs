namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Wake-capable network adapter. Schema: WakeAdapter.</summary>
/// <param name="MacAddress">Format AA-BB-CC-DD-EE-FF, uppercase.</param>
public sealed record WakeAdapter(string Name, string MacAddress, string Ipv4Address, string BroadcastAddress);
