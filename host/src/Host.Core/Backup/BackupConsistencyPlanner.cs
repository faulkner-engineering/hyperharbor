using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Backup;

public enum ConsistencyAction
{
    /// <summary>Do not back up now; <see cref="ConsistencyPlan.Reason"/> says why.</summary>
    Skip,

    /// <summary>The VM is off: its disks are not in use and are copied as they are.</summary>
    DirectCopy,

    /// <summary>Take a checkpoint and back up the frozen parent disk.</summary>
    Checkpoint,

    /// <summary>Copy the disks of a running VM that cannot be checkpointed (GPU-P), crash consistent.</summary>
    LiveCopy,
}

public enum SkipReason
{
    None,
    ExistingCheckpoints,
    NeedsOff,
    UnstableState,
    Generation1Unsupported,
}

public sealed record ConsistencyPlan(ConsistencyAction Action, SkipReason Reason, string? Warning)
{
    public static ConsistencyPlan Skip(SkipReason reason, string warning) => new(ConsistencyAction.Skip, reason, warning);
}

/// <summary>What the planner needs to know about the VM, read just before the run.</summary>
public sealed record ConsistencyInputs(
    VmState State,
    bool HasGpuPartition,
    GpuLiveCopy GpuLiveCopy,
    int ForeignCheckpoints,
    bool DisksAlreadyDifferencing);

/// <summary>Decides how a VM's disks are read so the copy is consistent. Pure.</summary>
public static class BackupConsistencyPlanner
{
    public static ConsistencyPlan Plan(ConsistencyInputs input)
    {
        if (input.ForeignCheckpoints > 0 || input.DisksAlreadyDifferencing)
        {
            return ConsistencyPlan.Skip(SkipReason.ExistingCheckpoints,
                "This VM has checkpoints of its own. Delete or merge them in Hyper-V, then back up again; HyperHarbor does not flatten them.");
        }

        return input.State switch
        {
            VmState.Off => new ConsistencyPlan(ConsistencyAction.DirectCopy, SkipReason.None, null),
            VmState.Running when input.HasGpuPartition && input.GpuLiveCopy == GpuLiveCopy.RequireOff =>
                ConsistencyPlan.Skip(SkipReason.NeedsOff, "This VM uses a GPU partition and cannot be checkpointed. It is backed up when it is off."),
            VmState.Running when input.HasGpuPartition =>
                new ConsistencyPlan(ConsistencyAction.LiveCopy, SkipReason.None,
                    "This VM uses a GPU partition and was copied while running. The copy is crash consistent: files the guest was writing may be incomplete."),
            VmState.Running => new ConsistencyPlan(ConsistencyAction.Checkpoint, SkipReason.None, null),
            _ => ConsistencyPlan.Skip(SkipReason.UnstableState, $"The VM is {input.State.ToString().ToLowerInvariant()}. It is backed up when it is running or off."),
        };
    }
}
