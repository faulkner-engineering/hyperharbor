using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Virtual machine summary. Schema: Vm.</summary>
/// <param name="Id">Hyper-V virtual machine ID.</param>
/// <param name="RdpAvailable">True when the VM accepted a TCP connection on port 3389 from the host.</param>
/// <param name="Provisioned">True when the calling device's User has a verified account on this VM.</param>
/// <param name="RemoteDesktop">Where clients connect for Remote Desktop.</param>
/// <param name="GuestOs">Guest operating system reported through Hyper-V data exchange.</param>
/// <param name="PerformanceMode">True when Performance mode (GPU partition and Remote Desktop tuning) is on.</param>
/// <param name="InstallState">The unattended install while it runs or after it failed; omitted otherwise.</param>
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
    VmGuestOs? GuestOs = null,
    Unattend.UnattendedInstallState? InstallState = null,
    bool PerformanceMode = false);

/// <summary>Remote Desktop endpoint of a VM. Schema: VmRemoteDesktop.</summary>
/// <param name="Address">Guest address clients connect to, or null when none is reported. Sent as null (required in the schema).</param>
/// <param name="ReachableFromHost">Same as Vm.RdpAvailable.</param>
public sealed record VmRemoteDesktop(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Address,
    bool ReachableFromHost);
