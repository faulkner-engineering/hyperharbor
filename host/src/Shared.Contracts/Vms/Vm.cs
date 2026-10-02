namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Virtual machine summary. Schema: Vm.</summary>
/// <param name="Id">Hyper-V virtual machine ID.</param>
/// <param name="RdpAvailable">True when the guest reports an address reachable for RDP.</param>
public sealed record Vm(
    Guid Id,
    string Name,
    VmState State,
    long? UptimeSeconds,
    int? CpuUsagePercent,
    long? MemoryAssignedMb,
    int Generation,
    bool RdpAvailable,
    IReadOnlyList<string> IpAddresses);
