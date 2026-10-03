using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmDeletionServiceTests : IDisposable
{
    private const string Folder = @"C:\VMs\Dev";
    private const string BaseDisk = @"C:\VMs\Dev\Dev.vhdx";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid OtherVmId = Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDiskFiles _files = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly VmDeletionService _deletion;

    public VmDeletionServiceTests()
    {
        _deletion = new VmDeletionService(_inventory, _storage, _files, _jobs, NullLogger<VmDeletionService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _storage.Attach(VmId, "Dev", BaseDisk);
        _files.Files.Add(BaseDisk);
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task Preview_OfASimpleVm_ListsItsDisk_WithNoBlockers()
    {
        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);

        Assert.Equal("Dev", preview.VmName);
        Assert.Equal([BaseDisk], preview.Disks);
        Assert.Empty(preview.Blockers);
        Assert.Equal(0, preview.CheckpointCount);
    }

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    [InlineData(VmState.Starting)]
    public async Task Delete_RefusesAVmThatIsNotOff(VmState state)
    {
        _inventory.SetState(VmId, state);

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: false));

        Assert.Equal(DeleteBlockerCode.NotOff, Assert.Single(preview.Blockers).Code);
        Assert.Contains("Dev", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_storage.DeletedVms);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Delete_RefusesWhenAnotherVmsDifferencingDiskUsesThisDiskAsItsParent()
    {
        const string child = @"C:\VMs\Other\Other.vhdx";
        _storage.Attach(OtherVmId, "Other", child);
        _storage.Parents[child] = BaseDisk;

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true));

        var blocker = Assert.Single(preview.Blockers);
        Assert.Equal(DeleteBlockerCode.SharedDisk, blocker.Code);
        Assert.Equal(DeleteBlockerScope.DeleteDisksOrCheckpoints, blocker.Scope);
        Assert.Contains("Other", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_storage.DeletedVms);
        Assert.Empty(_files.Deleted);
        Assert.Contains(BaseDisk, _files.Files);
    }

    [Fact]
    public async Task SharedParentDisk_StillAllowsDeletingTheVmAlone()
    {
        const string child = @"C:\VMs\Other\Other.vhdx";
        _storage.Attach(OtherVmId, "Other", child);
        _storage.Parents[child] = BaseDisk;

        var job = await StartAsync(deleteDisks: false);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
        Assert.Equal([VmId], _storage.DeletedVms);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Delete_RefusesWhenTheDiskIsAttachedToAnotherVm()
    {
        _storage.Attach(OtherVmId, "Other", BaseDisk);

        await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true));

        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Delete_RefusesWhenOnlyAnotherVmsCheckpointUsesTheDisk()
    {
        _storage.Attach(OtherVmId, "Other", BaseDisk, inCheckpoint: true);

        await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true));
    }

    [Fact]
    public async Task Delete_RefusesWhenAnUnattachedDifferencingDiskInTheFolderDependsOnTheDisk()
    {
        const string stray = @"C:\VMs\Dev\Experiment.vhdx";
        _files.Files.Add(stray);
        _storage.Parents[stray] = BaseDisk;

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);

        var blocker = Assert.Single(preview.Blockers);
        Assert.Equal(DeleteBlockerCode.SharedDisk, blocker.Code);
        Assert.Contains("Experiment.vhdx", blocker.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true));
    }

    [Fact]
    public async Task OwnDifferencingDisk_IsDeleted_ButItsTemplateParentIsKept()
    {
        const string template = @"C:\Templates\Windows11.vhdx";
        _storage.Disks.Clear();
        _files.Files.Clear();
        const string differencing = @"C:\VMs\Dev\Dev-diff.vhdx";
        _storage.Attach(VmId, "Dev", differencing);
        _storage.Parents[differencing] = template;
        _files.Files.UnionWith([differencing, template]);

        var job = await StartAsync(deleteDisks: true);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal([differencing], _files.Deleted);
        Assert.Contains(template, _files.Files);
    }

    [Fact]
    public async Task Checkpoints_AreFollowedToTheBaseDisk_AndMustBeChosenForDeletion()
    {
        const string current = @"C:\VMs\Dev\Dev_ABC.avhdx";
        _storage.Disks.Clear();
        _storage.Attach(VmId, "Dev", current);
        _storage.Attach(VmId, "Dev", BaseDisk, inCheckpoint: true);
        _storage.Parents[current] = BaseDisk;
        _storage.Checkpoints[VmId] = 1;
        _files.Files.Add(current);

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);
        Assert.Equal(1, preview.CheckpointCount);
        Assert.Equal([BaseDisk], preview.Disks);
        Assert.Empty(preview.Blockers);

        var refused = await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true, deleteCheckpoints: false));
        Assert.Contains("checkpoint", refused.Message, StringComparison.Ordinal);

        var job = await StartAsync(deleteDisks: true, deleteCheckpoints: true);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
        Assert.Equal([VmId], _storage.CheckpointsDeleted);
        Assert.Equal([BaseDisk], _files.Deleted);
    }

    [Fact]
    public async Task CheckpointTreeFiles_InTheFolder_AreNotMistakenForStrangers()
    {
        const string older = @"C:\VMs\Dev\Dev_OLD.avhdx";
        const string current = @"C:\VMs\Dev\Dev_NEW.avhdx";
        _storage.Disks.Clear();
        _storage.Attach(VmId, "Dev", current);
        _storage.Attach(VmId, "Dev", older, inCheckpoint: true);
        _storage.Parents[current] = older;
        _storage.Parents[older] = BaseDisk;
        _storage.Checkpoints[VmId] = 2;
        _files.Files.UnionWith([older, current]);

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);

        Assert.Empty(preview.Blockers);
        Assert.Equal([BaseDisk], preview.Disks);
    }

    [Fact]
    public async Task ConfirmName_MustMatchExactly()
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _deletion.StartAsync(VmId, UserId, new VmDeleteRequest(false, false, "dev"), null, CancellationToken.None));

        Assert.Equal("confirmName", Assert.Single(ex.Errors).Field);
        Assert.Empty(_storage.DeletedVms);
    }

    [Fact]
    public async Task UndeletableDisk_BlocksOnlyDiskDeletion()
    {
        _files.Undeletable.Add(BaseDisk);

        var preview = await _deletion.PreviewAsync(VmId, CancellationToken.None);
        Assert.Equal(DeleteBlockerScope.DeleteDisks, Assert.Single(preview.Blockers).Scope);
        await Assert.ThrowsAsync<LifecycleConflictException>(() => StartAsync(deleteDisks: true));

        var job = await StartAsync(deleteDisks: false);
        await _jobs.WhenFinished(job.Id);
        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
    }

    [Fact]
    public async Task DiskThatFailsToDelete_FailsTheJob_AfterTheVmIsGone()
    {
        _files.FailOnDelete.Add(BaseDisk);

        var job = await StartAsync(deleteDisks: true);
        await _jobs.WhenFinished(job.Id);

        var failed = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.Contains(BaseDisk, failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal([VmId], _storage.DeletedVms);
    }

    [Fact]
    public async Task VmStartedAfterTheRequest_FailsTheJob_WithoutDeleting()
    {
        // The job re-checks once it holds the lock; simulate the VM starting in between.
        var job = await StartAsync(deleteDisks: true, beforeJob: () => _inventory.SetState(VmId, VmState.Running));
        await _jobs.WhenFinished(job.Id);

        var failed = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.Empty(_storage.DeletedVms);
        Assert.Empty(_files.Deleted);
    }

    [Fact]
    public async Task Delete_TakesTheVmLock_SoPowerActionsWait()
    {
        var release = new TaskCompletionSource();
        _storage.BeforeDeleteVm = () => release.Task.Wait();
        var locks = new VmOperationLocks();
        using var jobs = new VmJobStore(locks, TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var deletion = new VmDeletionService(_inventory, _storage, _files, jobs, NullLogger<VmDeletionService>.Instance);

        var job = await deletion.StartAsync(VmId, UserId, new VmDeleteRequest(false, false, "Dev"), null, CancellationToken.None);

        Assert.NotNull(locks.HolderOf(VmId));
        Assert.Throws<VmBusyException>(() => locks.Acquire(VmId, "power action Start"));
        release.SetResult();
        await jobs.WhenFinished(job.Id);
    }

    [Fact]
    public async Task UnknownVm_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<VmNotFoundException>(() => _deletion.PreviewAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private async Task<VmJobSnapshot> StartAsync(bool deleteDisks, bool deleteCheckpoints = false, Action? beforeJob = null)
    {
        if (beforeJob is null)
        {
            return await _deletion.StartAsync(VmId, UserId, new VmDeleteRequest(deleteDisks, deleteCheckpoints, "Dev"), null, CancellationToken.None);
        }

        // Change the state after the request is checked but before the job's own check.
        var inventory = new SwitchingInventory(_inventory, beforeJob);
        var deletion = new VmDeletionService(inventory, _storage, _files, _jobs, NullLogger<VmDeletionService>.Instance);
        return await deletion.StartAsync(VmId, UserId, new VmDeleteRequest(deleteDisks, deleteCheckpoints, "Dev"), null, CancellationToken.None);
    }

    /// <summary>Runs an action after the first inventory read, so the job's second read sees a change.</summary>
    private sealed class SwitchingInventory(FakeVmInventory inner, Action afterFirstRead) : Core.IVmInventory
    {
        private int _reads;

        public Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken) => inner.ListAsync(cancellationToken);

        public async Task<Vm?> GetAsync(Guid vmId, CancellationToken cancellationToken)
        {
            var vm = await inner.GetAsync(vmId, cancellationToken);
            if (Interlocked.Increment(ref _reads) == 1)
            {
                afterFirstRead();
            }

            return vm;
        }

        public Task<string?> FindNameAsync(Guid vmId, CancellationToken cancellationToken) => inner.FindNameAsync(vmId, cancellationToken);
    }
}
