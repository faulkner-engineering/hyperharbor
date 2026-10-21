using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>What a transfer leaves on the source host.</summary>
public enum TransferMode
{
    /// <summary>The VM keeps its ID and MAC addresses and is deleted from the source once the target has it.</summary>
    Move,

    /// <summary>The target gets a new VM with a new ID and new dynamic MAC addresses; the source keeps its VM.</summary>
    Copy,
}

/// <summary>Why a VM cannot be transferred as it is.</summary>
public enum TransferBlockerCode
{
    /// <summary>The VM is running, paused, or in another state that is not Off.</summary>
    NotOff,

    /// <summary>The VM has a saved state, which is not transferred.</summary>
    SavedState,

    /// <summary>The VM has checkpoints. Merging them (deleting the checkpoints) clears this.</summary>
    Checkpoints,

    /// <summary>A disk is a differencing disk with a parent that is not one of the VM's checkpoints.</summary>
    DifferencingDisk,

    /// <summary>Another VM uses one of the VM's disks.</summary>
    SharedDisk,

    /// <summary>A disk file is missing or cannot be read.</summary>
    DiskMissing,

    /// <summary>An unattended install is still running on the VM.</summary>
    InstallInProgress,
}

public sealed record TransferBlocker(TransferBlockerCode Code, string Message);

/// <summary>One disk the source sends, attached at <paramref name="ControllerType"/> <paramref name="ControllerNumber"/>, <paramref name="Lun"/>.</summary>
/// <param name="Path">The disk's path on the source host.</param>
public sealed record TransferDisk(string Path, long SizeBytes, string ControllerType, int ControllerNumber, int Lun)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// What the source host reports about a VM before a transfer: enough for the target's preflight and for the
/// client's dialog. <see cref="Blockers"/> must be empty before the transfer can start.
/// </summary>
/// <param name="ConfigurationVersion">Hyper-V's configuration version, such as "12.0"; null when it could not be read.</param>
/// <param name="CheckpointCount">Checkpoints that would have to be merged first.</param>
/// <param name="Warnings">Things that change for the VM on the way, in sentences for the dialog.</param>
public sealed record TransferPreview(
    Guid VmId,
    string Name,
    VmState State,
    int Generation,
    string? ConfigurationVersion,
    ComputeState Compute,
    bool SecureBoot,
    string? SecureBootTemplateId,
    bool TpmEnabled,
    bool HasGpuPartition,
    IReadOnlyList<VmAdapterDefinition> Adapters,
    IReadOnlyList<TransferDisk> Disks,
    int CheckpointCount,
    IReadOnlyList<TransferBlocker> Blockers,
    IReadOnlyList<string> Warnings)
{
    public long TotalBytes => Disks.Sum(disk => disk.SizeBytes);
}

/// <summary>How one network adapter is connected on the target.</summary>
/// <param name="SwitchId">Null: not connected.</param>
/// <param name="StaticMacAddress">Twelve hexadecimal digits for a static address; null for a dynamic one.</param>
public sealed record TransferAdapterPlan(string? SwitchId, string? StaticMacAddress);

/// <summary>Where one received disk goes on the target.</summary>
/// <param name="SourcePath">The disk's path on the source host, as the exported definition still names it.</param>
public sealed record TransferDiskPlan(string SourcePath, string TargetPath, long SizeBytes)
{
    public string SourceFileName => Path.GetFileName(SourcePath);
}

/// <summary>
/// The target host's answer to a proposed transfer: the name and folders it will use, how it maps the network,
/// and whether it can accept the VM at all. <see cref="Errors"/> must be empty to accept; warnings need the
/// user's acknowledgement.
/// </summary>
/// <param name="VmId">The ID the VM keeps (a move), or null when Hyper-V assigns a new one (a copy).</param>
/// <param name="VmFolder">The VM's own folder: configuration files here, disks in its "Virtual Hard Disks" subfolder. A rollback deletes only this folder.</param>
public sealed record TransferPlan(
    TransferMode Mode,
    Guid? VmId,
    string Name,
    string VmFolder,
    IReadOnlyList<TransferDiskPlan> Disks,
    IReadOnlyList<TransferAdapterPlan> Adapters,
    IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings)
{
    public bool CanAccept => Errors.Count == 0;
}
