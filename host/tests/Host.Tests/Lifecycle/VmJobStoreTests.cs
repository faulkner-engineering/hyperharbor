using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmJobStoreTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly VmOperationLocks _locks = new();
    private readonly VmJobStore _jobs;

    public VmJobStoreTests()
    {
        _jobs = new VmJobStore(_locks, _time, NullLogger<VmJobStore>.Instance, maxFinishedJobs: 3);
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task Job_ReportsProgress_ThenSucceeds()
    {
        var release = new TaskCompletionSource();
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", async context =>
        {
            context.Report("Deleting disks", 60);
            await release.Task;
        });

        Assert.Equal(VmJobState.Running, job.State);
        Assert.Equal("Checking", job.Step);
        await WaitUntil(() => _jobs.Get(job.Id, UserId)!.Step == "Deleting disks");
        Assert.Equal(60, _jobs.Get(job.Id, UserId)!.PercentComplete);

        release.SetResult();
        await _jobs.WhenFinished(job.Id);

        var done = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Succeeded, done.State);
        Assert.Equal(100, done.PercentComplete);
        Assert.Null(done.ErrorDetail);
    }

    [Fact]
    public async Task Job_HoldsTheVmLock_UntilItEnds()
    {
        var release = new TaskCompletionSource();
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => release.Task);

        Assert.Equal(VmJobStore.Describe(VmJobKind.DeleteVm), _locks.HolderOf(VmId));
        Assert.Throws<VmBusyException>(() => _jobs.Start(VmJobKind.ApplyCompute, VmId, UserId, "Checking", _ => Task.CompletedTask));

        release.SetResult();
        await _jobs.WhenFinished(job.Id);
        Assert.Null(_locks.HolderOf(VmId));
    }

    [Fact]
    public void Start_WhenTheVmIsBusy_ThrowsAndCreatesNoJob()
    {
        using var held = _locks.Acquire(VmId, "power action Start");

        var ex = Assert.Throws<VmBusyException>(() => _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => Task.CompletedTask));

        Assert.Equal("power action Start", ex.Holder);
    }

    [Fact]
    public async Task Failure_FromHyperHarbor_KeepsItsMessage_AndReleasesTheLock()
    {
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", context =>
        {
            context.Report("Deleting the virtual machine", 40);
            throw new HyperVUnavailableException("Hyper-V is not enabled.");
        });
        await _jobs.WhenFinished(job.Id);

        var failed = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.Equal("Deleting the virtual machine failed", failed.ErrorTitle);
        Assert.Equal("Hyper-V is not enabled.", failed.ErrorDetail);
        Assert.Equal(40, failed.PercentComplete);
        Assert.Null(_locks.HolderOf(VmId));
    }

    [Fact]
    public async Task UnexpectedFailure_HidesItsMessage()
    {
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => throw new InvalidOperationException(@"C:\secret\path is broken"));
        await _jobs.WhenFinished(job.Id);

        var failed = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.DoesNotContain("secret", failed.ErrorDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnFinished_ReceivesTheFinalSnapshot()
    {
        VmJobSnapshot? finished = null;
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => Task.CompletedTask, snapshot => finished = snapshot);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(job.Id, finished?.Id);
        Assert.Equal(VmJobState.Succeeded, finished?.State);
    }

    [Fact]
    public async Task Job_IsVisibleOnlyToItsUser()
    {
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => Task.CompletedTask);
        await _jobs.WhenFinished(job.Id);

        Assert.NotNull(_jobs.Get(job.Id, UserId));
        Assert.Null(_jobs.Get(job.Id, Guid.NewGuid()));
        Assert.Null(_jobs.Get(Guid.NewGuid(), UserId));
    }

    [Fact]
    public async Task AttachVm_RecordsTheVm_AndTakesItsLock()
    {
        var newVm = Guid.NewGuid();
        var attached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var job = _jobs.Start(VmJobKind.CreateVm, null, UserId, "Creating the disk", async context =>
        {
            context.AttachVm(newVm);
            attached.SetResult();
            await release.Task;
        });

        await attached.Task;
        Assert.Equal(newVm, _jobs.Get(job.Id, UserId)!.VmId);
        Assert.Equal(VmJobStore.Describe(VmJobKind.CreateVm), _locks.HolderOf(newVm));

        release.SetResult();
        await _jobs.WhenFinished(job.Id);
        Assert.Null(_locks.HolderOf(newVm));
    }

    [Fact]
    public async Task FinishedJobs_ArePrunedAfterTheRetention()
    {
        var job = _jobs.Start(VmJobKind.DeleteVm, VmId, UserId, "Checking", _ => Task.CompletedTask);
        await _jobs.WhenFinished(job.Id);

        _time.Advance(VmJobStore.DefaultRetention - TimeSpan.FromSeconds(1));
        Assert.NotNull(_jobs.Get(job.Id, UserId));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(_jobs.Get(job.Id, UserId));
    }

    [Fact]
    public async Task FinishedJobs_BeyondTheCap_DropTheOldestFirst()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 4; index++)
        {
            var job = _jobs.Start(VmJobKind.DeleteVm, Guid.NewGuid(), UserId, "Checking", _ => Task.CompletedTask);
            await _jobs.WhenFinished(job.Id);
            ids.Add(job.Id);
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        // Pruning runs when the next job starts.
        var running = new TaskCompletionSource();
        _jobs.Start(VmJobKind.DeleteVm, Guid.NewGuid(), UserId, "Checking", _ => running.Task);

        Assert.Null(_jobs.Get(ids[0], UserId));
        Assert.All(ids.Skip(1), id => Assert.NotNull(_jobs.Get(id, UserId)));
        running.SetResult();
    }

    [Fact]
    public void Locks_AreIndependentPerVm_AndReusableAfterRelease()
    {
        var other = Guid.NewGuid();
        var first = _locks.Acquire(VmId, "deleteVm");
        using (_locks.Acquire(other, "applyCompute"))
        {
            Assert.Throws<VmBusyException>(() => _locks.Acquire(VmId, "power action Start"));
        }

        first.Dispose();
        first.Dispose();
        using var again = _locks.Acquire(VmId, "power action Start");
        Assert.Equal("power action Start", _locks.HolderOf(VmId));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the job.");
            await Task.Delay(10);
        }
    }
}
