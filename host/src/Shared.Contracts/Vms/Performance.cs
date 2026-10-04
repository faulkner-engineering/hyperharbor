using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>GPU maker, from the PCI vendor ID. Schema: GpuVendor.</summary>
public enum GpuVendor
{
    Nvidia,
    Amd,
    Intel,
    Other,
}

/// <summary>
/// The share of a partitionable GPU a VM gets. Each percent sets the partition's minimum, maximum, and
/// optimal values to that share of what the GPU offers. Schema: GpuPartitionShare.
/// </summary>
/// <param name="InstancePath">A partitionable GPU from GET /host/gpu. Null: the host's first partitionable GPU.</param>
public sealed record GpuPartitionShare(
    string? InstancePath = null,
    int VramPercent = 50,
    int EncodePercent = 50,
    int DecodePercent = 50,
    int ComputePercent = 50);

/// <summary>Memory-mapped I/O space for the GPU partition. Schema: MmioSettings.</summary>
/// <param name="LowGapMb">Below 4 GB, 128 to 3584 MB.</param>
/// <param name="HighGapMb">Above 4 GB.</param>
public sealed record MmioSettings(int LowGapMb = 1024, int HighGapMb = 32768);

/// <summary>Remote Desktop choices written into the guest. Schema: PerformanceRdpSettings.</summary>
/// <param name="HardwareEncoding">Experimental: prefer the GPU for H.264/AVC encoding.</param>
public sealed record PerformanceRdpSettings(bool HardwareEncoding = false);

/// <summary>
/// Performance mode for a Windows VM, applied while it is off. Dynamic memory is turned off, checkpoints
/// are disabled, the automatic stop action becomes Turn Off, and the guest controls cache types.
/// Schema: PerformanceSettings.
/// </summary>
/// <param name="MemoryMb">Fixed memory.</param>
/// <param name="MoveStorageTo">Moves the VM's files to this folder (for example on a faster volume); null keeps them.</param>
public sealed record PerformanceSettings(
    [property: JsonRequired] int ProcessorCount,
    [property: JsonRequired] long MemoryMb,
    GpuPartitionShare? Gpu = null,
    MmioSettings? Mmio = null,
    string? MoveStorageTo = null,
    PerformanceRdpSettings? Rdp = null,
    bool AcknowledgeWarnings = false);

/// <summary>The GPU driver copied into the guest, compared with the host's. Schema: GuestDriverStatus.</summary>
/// <param name="Drift">True when the host's driver changed since it was copied; copy it again.</param>
/// <param name="RebootRequired">Some guest files were in use; they are replaced when the guest restarts.</param>
public sealed record GuestDriverStatus(
    GpuVendor Vendor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? HostVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GuestVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CopiedAt,
    bool Drift,
    bool RebootRequired);

/// <summary>A VM's Performance mode. Schema: VmPerformance.</summary>
/// <param name="Settings">As applied; null when Performance mode is off.</param>
/// <param name="GpuAttached">True when the VM has a GPU partition adapter.</param>
/// <param name="Driver">Null until the guest has been set up.</param>
public sealed record VmPerformance(
    Guid VmId,
    bool Enabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] PerformanceSettings? Settings,
    bool GpuAttached,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] GuestDriverStatus? Driver,
    IReadOnlyList<ValidationIssue> Warnings);

/// <summary>A GPU on the host. Schema: HostGpuDevice.</summary>
/// <param name="InstancePath">The partitionable GPU's path for <see cref="GpuPartitionShare.InstancePath"/>; null when it cannot be partitioned.</param>
public sealed record HostGpuDevice(
    string Name,
    GpuVendor Vendor,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DriverVersion,
    bool Partitionable,
    int PartitionCount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? InstancePath);

/// <summary>GPU driver errors in the host's System log. Schema: GpuDriverWarning.</summary>
public sealed record GpuDriverWarning(string Provider, int EventId, int Count, DateTimeOffset LastSeen, string Message);

/// <summary>The host's GPUs and recent driver errors. Schema: HostGpu.</summary>
public sealed record HostGpu(IReadOnlyList<HostGpuDevice> Gpus, IReadOnlyList<GpuDriverWarning> Warnings);

/// <summary>Sets up a Performance mode guest. Schema: GuestSetupRequest.</summary>
/// <param name="DriversOnly">Copy the GPU driver only (a re-sync after the host's driver changed); leave the registry alone.</param>
public sealed record GuestSetupRequest(bool DriversOnly = false);
