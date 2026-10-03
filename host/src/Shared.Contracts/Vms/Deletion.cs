using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Deletes a virtual machine that is off. Schema: VmDeleteRequest.</summary>
/// <param name="DeleteDisks">Also delete the virtual hard disk files the VM uses. Parent disks are never deleted.</param>
/// <param name="DeleteCheckpoints">Required when the VM has checkpoints: they are merged into the VM's disks first.</param>
/// <param name="ConfirmName">Must equal the VM's name exactly.</param>
public sealed record VmDeleteRequest(
    [property: JsonRequired] bool DeleteDisks,
    [property: JsonRequired] bool DeleteCheckpoints,
    [property: JsonRequired] string ConfirmName);

/// <summary>Why a deletion cannot proceed. Schema: DeleteBlockerCode.</summary>
public enum DeleteBlockerCode
{
    /// <summary>The VM is not off.</summary>
    NotOff,

    /// <summary>Another VM, or a disk file outside this VM, depends on one of its disks.</summary>
    SharedDisk,

    /// <summary>The host service cannot delete one of the disk files.</summary>
    DiskNotDeletable,
}

/// <summary>Which deletions a blocker prevents. Schema: DeleteBlockerScope.</summary>
public enum DeleteBlockerScope
{
    Always,

    /// <summary>Only when deleteDisks is true.</summary>
    DeleteDisks,

    /// <summary>When deleteDisks or deleteCheckpoints is true (merging checkpoints writes to the disks).</summary>
    DeleteDisksOrCheckpoints,
}

/// <summary>Schema: DeleteBlocker.</summary>
public sealed record DeleteBlocker(DeleteBlockerCode Code, DeleteBlockerScope Scope, string Message);

/// <summary>What deleting a VM would do. Schema: VmDeletePreview.</summary>
/// <param name="Disks">The files deleteDisks would delete. Checkpoint files are merged into these first.</param>
/// <param name="Blockers">Reasons the deletion cannot proceed, each with the options it applies to.</param>
public sealed record VmDeletePreview(
    Guid VmId,
    string VmName,
    VmState State,
    int CheckpointCount,
    IReadOnlyList<string> Disks,
    IReadOnlyList<DeleteBlocker> Blockers);
