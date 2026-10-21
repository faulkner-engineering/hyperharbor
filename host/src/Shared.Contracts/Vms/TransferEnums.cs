namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>What a transfer leaves on the source host. Schema: TransferMode.</summary>
public enum TransferMode
{
    /// <summary>The VM keeps its ID and MAC addresses and is deleted from the source once the target has it.</summary>
    Move,

    /// <summary>The target gets a new VM with a new ID and new dynamic MAC addresses; the source keeps its VM.</summary>
    Copy,
}

/// <summary>Why a VM cannot be transferred as it is. Schema: TransferBlockerCode.</summary>
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

/// <summary>Where an incoming transfer is on the receiving host. Schema: IncomingTransferState.</summary>
public enum IncomingTransferState
{
    /// <summary>Files are arriving. Cancel, expiry, or a restart rolls the transfer back.</summary>
    Receiving,

    /// <summary>Hyper-V is importing the VM. A restart in this state rolls the transfer back, since the source was never told it worked.</summary>
    Importing,

    /// <summary>The VM exists on the receiving host and the source has been told. The receiving host never deletes it from here on.</summary>
    Imported,
}
