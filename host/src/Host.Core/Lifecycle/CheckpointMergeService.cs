using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// Deletes every checkpoint of a VM as a job, which merges their differencing disks into the VM's disks. The VM keeps
/// its current state; only the points to go back to are gone. A transfer needs this first.
/// </summary>
public sealed class CheckpointMergeService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVStorage _storage;
    private readonly VmJobStore _jobs;

    public CheckpointMergeService(IVmInventory inventory, IHyperVStorage storage, VmJobStore jobs)
    {
        _inventory = inventory;
        _storage = storage;
        _jobs = jobs;
    }

    /// <exception cref="VmNotFoundException">There is no such VM.</exception>
    /// <exception cref="LifecycleConflictException">The VM has no checkpoints.</exception>
    public async Task<VmJobSnapshot> StartAsync(Guid vmId, Guid userId, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var storage = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (storage.CheckpointsOf(vmId) == 0)
        {
            throw new LifecycleConflictException($"{vm.Name} has no checkpoints.");
        }

        return _jobs.Start(VmJobKind.MergeCheckpoints, vmId, userId, "Merging checkpoints",
            context => _storage.DeleteCheckpointsAsync(vmId, percent => context.Report("Merging checkpoints", percent), context.Stopping),
            onFinished);
    }
}
