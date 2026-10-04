using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Performance;

public sealed class VmPerformanceServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("5e4d3c2b-1a09-4f8e-9d7c-6b5a49382716");
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVPerformance _performance = new();
    private readonly FakeHyperVCompute _compute = new();
    private readonly FakeHostGpuReader _gpus = new();
    private readonly FakeGpuDriverSource _drivers = new();
    private readonly FakeGuestPerformanceSetup _guestSetup = new();
    private readonly Core.Provisioning.VmCredentialStore _credentials;
    private readonly VmOperationLocks _locks = new();
    private readonly VmJobStore _jobs;
    private readonly PerformanceStore _store;
    private readonly VmPerformanceService _service;

    public VmPerformanceServiceTests()
    {
        _jobs = new VmJobStore(_locks, TimeProvider.System, NullLogger<VmJobStore>.Instance);
        _store = new PerformanceStore(_directory);
        _credentials = new Core.Provisioning.VmCredentialStore(_directory);
        _service = new VmPerformanceService(_inventory, _performance, _compute, _gpus, new FakeHostCapacity(), _store, _credentials, _drivers, _guestSetup, _locks, _jobs,
            new LifecycleOptions(), new FakeTimeProvider(DateTimeOffset.Parse("2026-10-04T12:00:00Z")), NullLogger<VmPerformanceService>.Instance);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void Vm(VmState state)
    {
        _inventory.Vms.Clear();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", state));
        _compute.States[VmId] = new ComputeState(2, 4096, 8192, true, false, false, 1);
    }

    private static PerformanceSettings Settings(string? moveTo = null) =>
        new(8, 4096, new GpuPartitionShare(VramPercent: 75, EncodePercent: 50, DecodePercent: 50, ComputePercent: 100), MoveStorageTo: moveTo);

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    [InlineData(VmState.Starting)]
    public async Task Apply_ToAVmThatIsNotOff_IsRefusedWith409VmMustBeOff(VmState state)
    {
        Vm(state);

        var error = await Assert.ThrowsAsync<LifecycleConflictException>(() =>
            _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.VmMustBeOff, error.Code);
        Assert.Empty(_performance.Calls);
        Assert.Empty(_compute.Applied);
        Assert.Null(_store.Find(VmId));
    }

    [Fact]
    public async Task Apply_ToAnOffVm_SetsFixedResourcesAndTheGpuPartitionInAJob()
    {
        Vm(VmState.Off);

        var job = await _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
        Assert.Equal(VmJobKind.ApplyPerformance, job.Kind);
        var (_, change) = Assert.Single(_compute.Applied);
        var desired = _compute.States[VmId];
        Assert.Equal(8, change.ProcessorCount);
        Assert.False(change.DynamicMemory);
        Assert.Equal(4096, desired.StartupMemoryMb);
        Assert.Equal(4096, desired.MaximumMemoryMb);
        Assert.False(desired.DynamicMemory);

        var plan = _performance.Applied[VmId];
        Assert.Equal(750_000_000UL, plan.Gpu.Vram);
        Assert.Equal(1_000_000_000UL, plan.Gpu.Compute);
        Assert.Equal(1024, plan.LowMmioGapMb);
        Assert.Equal(32768, plan.HighMmioGapMb);
        Assert.Null(plan.Gpu.InstancePath);

        var record = _store.Find(VmId)!;
        Assert.Equal(FakeHostGpuReader.IntelPath, record.Settings.Gpu!.InstancePath);
        Assert.False(record.Settings.Rdp!.HardwareEncoding);
    }

    [Fact]
    public async Task Apply_WithAStorageFolder_MovesTheVmAfterConfiguringIt()
    {
        Vm(VmState.Off);
        var folder = Path.Combine(Path.GetTempPath(), "Fast VMs");

        var job = await _service.ApplyAsync(VmId, UserId, Settings(folder), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(["apply", "move"], _performance.Calls);
        Assert.Equal([(VmId, folder)], _performance.Moves);
        Assert.Null(_store.Find(VmId)!.Settings.MoveStorageTo);
    }

    [Fact]
    public async Task Apply_WithoutAPartitionableGpu_IsRefusedWithGpuUnavailable()
    {
        Vm(VmState.Off);
        _gpus.Gpus[0] = _gpus.Gpus[0] with { InstancePath = null, Capacity = null };

        var error = await Assert.ThrowsAsync<LifecycleConflictException>(() =>
            _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.GpuUnavailable, error.Code);
        Assert.Empty(_performance.Calls);
    }

    [Fact]
    public async Task Remove_RefusesARunningVm_AndRemovesFromAnOffOne()
    {
        Vm(VmState.Off);
        var job = await _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Vm(VmState.Running);
        var error = await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.RemoveAsync(VmId, CancellationToken.None));
        Assert.Equal(ContractInfo.ProblemCodes.VmMustBeOff, error.Code);

        Vm(VmState.Off);
        await _service.RemoveAsync(VmId, CancellationToken.None);
        Assert.Null(_store.Find(VmId));
        Assert.False((await _service.GetAsync(VmId, CancellationToken.None)).GpuAttached);
    }

    [Fact]
    public async Task Get_WarnsWhenTheAdapterWasRemovedOutsideHyperHarbor()
    {
        Vm(VmState.Off);
        var job = await _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        _performance.Applied.Remove(VmId);

        var performance = await _service.GetAsync(VmId, CancellationToken.None);

        Assert.True(performance.Enabled);
        Assert.False(performance.GpuAttached);
        Assert.Contains(performance.Warnings, warning => warning.Field == "gpu");
    }

    [Fact]
    public async Task Drift_Fires_WhenTheHostDriverVersionChanges()
    {
        Vm(VmState.Off);
        _store.Save(new PerformanceRecord(VmId, Settings() with { Gpu = new GpuPartitionShare(FakeHostGpuReader.IntelPath) }, DateTimeOffset.UtcNow,
            new GuestDriverRecord(GpuVendor.Intel, "30.0.101.1122", ["iigd_dch.inf_amd64_6091bde938afd934"], DateTimeOffset.UtcNow, false)));

        var same = (await _service.GetAsync(VmId, CancellationToken.None)).Driver!;
        Assert.False(same.Drift);
        Assert.Equal("30.0.101.1122", same.HostVersion);

        _gpus.DriverVersion = "31.0.101.5186";
        var drifted = (await _service.GetAsync(VmId, CancellationToken.None)).Driver!;

        Assert.True(drifted.Drift);
        Assert.Equal("31.0.101.5186", drifted.HostVersion);
        Assert.Equal("30.0.101.1122", drifted.GuestVersion);
    }
    private async Task EnableAsync()
    {
        Vm(VmState.Off);
        var job = await _service.ApplyAsync(VmId, UserId, Settings(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        _inventory.Vms[0] = _inventory.Vms[0] with { State = VmState.Running };
    }

    [Fact]
    public async Task GuestSetup_WithoutAStoredCredential_Returns409CredentialRequired()
    {
        await EnableAsync();

        var error = await Assert.ThrowsAsync<LifecycleConflictException>(() =>
            _service.StartGuestSetupAsync(VmId, UserId, driversOnly: false, null, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.CredentialRequired, error.Code);
        Assert.Empty(_guestSetup.Runs);
    }

    [Fact]
    public async Task GuestSetup_NeedsPerformanceModeAndARunningWindowsGuest()
    {
        _credentials.Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", "Admin-Pass1!"));
        Vm(VmState.Running);
        await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.StartGuestSetupAsync(VmId, UserId, false, null, CancellationToken.None));

        await EnableAsync();
        _inventory.Vms[0] = _inventory.Vms[0] with { State = VmState.Off };
        await Assert.ThrowsAsync<LifecycleConflictException>(() => _service.StartGuestSetupAsync(VmId, UserId, false, null, CancellationToken.None));
    }

    [Fact]
    public async Task GuestSetup_CopiesTheDriverWritesThePolicyAndRecordsTheVersion()
    {
        await EnableAsync();
        _credentials.Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", "Admin-Pass1!"));
        _guestSetup.RebootRequired = true;

        var job = await _service.StartGuestSetupAsync(VmId, UserId, driversOnly: false, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
        var run = Assert.Single(_guestSetup.Runs);
        Assert.Contains(run.Registry!, value => value.Name == "AVCHardwareEncodePreferred" && value.Value == 0);
        var driver = (await _service.GetAsync(VmId, CancellationToken.None)).Driver!;
        Assert.Equal("30.0.101.1122", driver.GuestVersion);
        Assert.True(driver.RebootRequired);
        Assert.False(driver.Drift);
        Assert.Equal(["iigd_dch.inf_amd64_6091bde938afd934"], _store.Find(VmId)!.Guest!.DriverFolders);
    }

    [Fact]
    public async Task DriversOnlyResync_ClearsDrift_WithoutTouchingTheRegistry()
    {
        await EnableAsync();
        _credentials.Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", "Admin-Pass1!"));
        var first = await _service.StartGuestSetupAsync(VmId, UserId, false, null, CancellationToken.None);
        await _jobs.WhenFinished(first.Id);

        _gpus.DriverVersion = "31.0.101.5186";
        Assert.True((await _service.GetAsync(VmId, CancellationToken.None)).Driver!.Drift);

        var resync = await _service.StartGuestSetupAsync(VmId, UserId, driversOnly: true, null, CancellationToken.None);
        await _jobs.WhenFinished(resync.Id);

        Assert.Null(_guestSetup.Runs[^1].Registry);
        var driver = (await _service.GetAsync(VmId, CancellationToken.None)).Driver!;
        Assert.False(driver.Drift);
        Assert.Equal("31.0.101.5186", driver.GuestVersion);
    }

    [Fact]
    public async Task GuestSetupErrors_DoNotLeakTheAdminPassword()
    {
        await EnableAsync();
        _credentials.Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", "Admin-Pass1!"));
        _guestSetup.Failure = new Core.Provisioning.GuestOperationException("Copy failed for hhadmin / Admin-Pass1!.");

        var job = await _service.StartGuestSetupAsync(VmId, UserId, false, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        var finished = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, finished.State);
        Assert.DoesNotContain("Admin-Pass1!", finished.ErrorDetail, StringComparison.Ordinal);
        Assert.Null(_store.Find(VmId)!.Guest);
    }
}
