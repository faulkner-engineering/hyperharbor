using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Checks to fix automatically. Schema: WakeFixRequest.</summary>
public sealed record WakeFixRequest([property: JsonRequired] IReadOnlyList<string> CheckIds);
