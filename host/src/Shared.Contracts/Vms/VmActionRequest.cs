using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Request body for a power action. Schema: VmActionRequest.</summary>
/// <remarks>Action is required so that an empty body cannot default to <see cref="VmAction.Start"/>.</remarks>
public sealed record VmActionRequest([property: JsonRequired] VmAction Action);
