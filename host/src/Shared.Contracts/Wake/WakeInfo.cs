namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Adapter details a client needs to wake the host. Schema: WakeInfo.</summary>
public sealed record WakeInfo(IReadOnlyList<WakeAdapter> Adapters);
