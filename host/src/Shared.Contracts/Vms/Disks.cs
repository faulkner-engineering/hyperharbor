using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Why a disk cannot grow now. Schema: DiskGrowBlocker.</summary>
public enum DiskGrowBlocker
{
    /// <summary>The VM has checkpoints; merging them first lets the disk grow.</summary>
    Checkpoints,

    /// <summary>A differencing disk, which grows only with its parent.</summary>
    Differencing,

    /// <summary>Hyper-V cannot read the disk file.</summary>
    NotFound,

    /// <summary>The VM is saved or paused.</summary>
    VmState,
}

/// <summary>A virtual hard disk attached to a VM. Schema: VmDisk.</summary>
/// <param name="Path">The disk file on the host; growing names the disk with it.</param>
/// <param name="MaximumSizeBytes">The size the guest sees.</param>
/// <param name="FileSizeBytes">The space the file takes now.</param>
/// <param name="GrowWhileRunning">A VHDX on a Generation 2 VM, which grows without shutting the VM down.</param>
public sealed record VmDisk(
    string Path,
    long MaximumSizeBytes,
    long FileSizeBytes,
    bool GrowWhileRunning,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiskGrowBlocker? GrowBlocker);

/// <summary>A VM's virtual hard disks, in Hyper-V's order. Schema: VmDiskList.</summary>
public sealed record VmDiskList(Guid VmId, IReadOnlyList<VmDisk> Disks);

/// <summary>Increases a disk's maximum size. Schema: GrowVmDiskRequest.</summary>
/// <param name="SizeGb">The new maximum in GB (1024^3 bytes), larger than the current one.</param>
/// <param name="ExtendPartition">Also extend the guest's partition at the end of the disk; true when absent.</param>
/// <param name="ShutDownToApply">For a running VM whose disk cannot grow while it runs, shut it down first and start it again.</param>
public sealed record GrowVmDiskRequest(
    [property: JsonRequired] string Path,
    [property: JsonRequired] int SizeGb,
    bool ExtendPartition = true,
    bool ShutDownToApply = false,
    bool AcknowledgeWarnings = false);

/// <summary>What a growDisk job did. Schema: DiskGrowResult.</summary>
/// <param name="PartitionExtended">The partition at the end of the disk now uses the new space.</param>
/// <param name="Note">What the user still has to do, or null.</param>
public sealed record DiskGrowResult(
    string Path,
    long MaximumSizeBytes,
    bool PartitionExtended,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Note);
