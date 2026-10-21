using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class CheckpointMergeServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly CheckpointMergeService _merge;

    public CheckpointMergeServiceTests()
    {
        _merge = new CheckpointMergeService(_inventory, _storage, _jobs);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task Merge_DeletesEveryCheckpoint_AsAJob()
    {
        _storage.Checkpoints[VmId] = 2;
        var finished = new TaskCompletionSource<VmJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        var started = await _merge.StartAsync(VmId, UserId, finished.SetResult, CancellationToken.None);
        var job = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(VmJobKind.MergeCheckpoints, started.Kind);
        Assert.Equal(VmJobState.Succeeded, job.State);
        Assert.Equal([VmId], _storage.CheckpointsDeleted);
    }

    [Fact]
    public async Task AVmWithoutCheckpoints_IsAConflict()
    {
        await Assert.ThrowsAsync<LifecycleConflictException>(() => _merge.StartAsync(VmId, UserId, null, CancellationToken.None));
        Assert.Empty(_storage.CheckpointsDeleted);
    }

    [Fact]
    public async Task AnUnknownVm_IsNotFound()
    {
        await Assert.ThrowsAsync<VmNotFoundException>(() => _merge.StartAsync(Guid.NewGuid(), UserId, null, CancellationToken.None));
    }
}
