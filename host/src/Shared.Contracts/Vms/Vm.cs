namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Virtual machine summary. Schema: Vm.</summary>
/// <param name="Id">Hyper-V virtual machine ID.</param>
/// <param name="RdpAvailable">True when the VM accepted a TCP connection on port 3389 from the host.</param>
/// <param name="Provisioned">True when the calling device's User has a verified account on this VM.</param>
/// <param name="RemoteDesktop">Where clients connect for Remote Desktop.</param>
/// <param name="GuestOs">Guest operating system reported through Hyper-V data exchange.</param>
public sealed record Vm(
    Guid Id,
    string Name,
    VmState State,
    long? UptimeSeconds,
    int? CpuUsagePercent,
    long? MemoryAssignedMb,
    int Generation,
    bool RdpAvailable,
    IReadOnlyList<string> IpAddresses,
    bool Provisioned = false,
    VmRemoteDesktop? RemoteDesktop = null,
    VmGuestOs? GuestOs = null);

/// <summary>Remote Desktop endpoint of a VM. Schema: VmRemoteDesktop.</summary>
/// <param name="Address">Guest address clients connect to, or null when none is reported.</param>
/// <param name="ReachableFromHost">Same as Vm.RdpAvailable.</param>
public sealed record VmRemoteDesktop(string? Address, bool ReachableFromHost);
