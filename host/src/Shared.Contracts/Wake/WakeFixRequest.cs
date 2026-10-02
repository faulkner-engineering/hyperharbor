namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Checks to fix automatically. Schema: WakeFixRequest.</summary>
public sealed record WakeFixRequest(IReadOnlyList<string> CheckIds);
