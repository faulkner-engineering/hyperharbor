using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>A processor or memory setting. Schema: ComputeSetting.</summary>
public enum ComputeSetting
{
    ProcessorCount,
    StartupMemoryMb,
    MaximumMemoryMb,
    DynamicMemory,
    NestedVirtualization,
    MacAddressSpoofing,
}

/// <summary>A VM's processor, memory, and related settings. Schema: VmComputeSettings.</summary>
/// <param name="MacAddressSpoofing">True when every connected network adapter allows MAC address spoofing.</param>
/// <param name="NetworkAdapterCount">Adapters connected to a switch; MAC spoofing applies to these.</param>
/// <param name="RequiresOff">Settings that can change only while the VM is off.</param>
public sealed record VmComputeSettings(
    Guid VmId,
    VmState State,
    int ProcessorCount,
    long StartupMemoryMb,
    long MaximumMemoryMb,
    bool DynamicMemory,
    bool NestedVirtualization,
    bool MacAddressSpoofing,
    int NetworkAdapterCount,
    IReadOnlyList<ComputeSetting> RequiresOff);

/// <summary>Changes to a VM's compute settings; omitted values stay as they are. Schema: UpdateVmComputeRequest.</summary>
/// <param name="ShutDownToApply">
/// When a running VM needs to be off for a change: shut the guest down, apply, and start it again, as a job.
/// </param>
/// <param name="AcknowledgeWarnings">Proceed despite host resource warnings.</param>
public sealed record UpdateVmComputeRequest(
    int? ProcessorCount = null,
    long? StartupMemoryMb = null,
    long? MaximumMemoryMb = null,
    bool? DynamicMemory = null,
    bool? NestedVirtualization = null,
    bool? MacAddressSpoofing = null,
    bool ShutDownToApply = false,
    bool AcknowledgeWarnings = false);

/// <summary>
/// Result of PATCH /vms/{vmId}/compute: the new settings when applied at once, or the job that shuts
/// the VM down to apply them. Exactly one is set. Schema: VmComputeUpdate.
/// </summary>
public sealed record VmComputeUpdate(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] VmComputeSettings? Settings,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] VmJob? Job);
