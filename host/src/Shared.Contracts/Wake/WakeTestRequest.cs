using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Wake;

/// <summary>Requests a sleep for a wake test. Schema: WakeTestRequest.</summary>
/// <param name="DelaySeconds">Between 5 and 300 seconds.</param>
public sealed record WakeTestRequest([property: JsonRequired] int DelaySeconds);
