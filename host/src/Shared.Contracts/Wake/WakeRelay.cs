using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Asks this host to send magic packets on its LAN for another, sleeping host. Schema: WakeRelayRequest.</summary>
public sealed record WakeRelayRequest([property: JsonRequired] IReadOnlyList<WakeAdapter> Adapters);

/// <summary>How many magic packets the relay sent. Schema: WakeRelayResult.</summary>
public sealed record WakeRelayResult([property: JsonRequired] int Sent);
