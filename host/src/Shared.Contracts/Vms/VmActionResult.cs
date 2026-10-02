namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Result of an accepted power action. Schema: VmActionResult.</summary>
public sealed record VmActionResult(Guid VmId, VmAction Action, bool Accepted, VmState State);
