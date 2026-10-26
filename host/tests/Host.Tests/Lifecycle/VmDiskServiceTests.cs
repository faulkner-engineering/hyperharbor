using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmDiskServiceTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string DiskPath = @"D:\VMs\Dev\Virtual Hard Disks\Dev.vhdx";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDisks _disks = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakeExtender _extender = new();
    private readonly VmCredentialStore _credentials;
    private readonly ShutdownPower _power;
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options = new();
    private readonly VmDiskService _service;

    public VmDiskServiceTests()
    {
        _credentials = new VmCredentialStore(_dataDirectory);
        _power = new ShutdownPower(_inventory);
        _jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        _service = new VmDiskService(_inventory, _storage, _disks, _files, _extender, _credentials, _power, _jobs, _options, TimeProvider.System, NullLogger<VmDiskService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _storage.Attach(VmId, "Dev", DiskPath);
        _files.Files.Add(DiskPath);
        _files.Sizes[DiskPath] = 20 * Gb;
        _disks.Disks[DiskPath] = new VirtualDiskInfo(64 * Gb, IsVhdx: true, ParentPath: null);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task List_ReportsSizes_AndThatAGeneration2VhdxGrowsWhileRunning()
    {
        var disk = Assert.Single((await _service.ListAsync(VmId, CancellationToken.None)).Disks);

        Assert.Equal(new VmDisk(DiskPath, 64 * Gb, 20 * Gb, GrowWhileRunning: true, GrowBlocker: null), disk);
    }

    [Fact]
    public async Task List_SaysWhyADiskCannotGrow()
    {
        _storage.Checkpoints[VmId] = 1;
        Assert.Equal(DiskGrowBlocker.Checkpoints, (await Single()).GrowBlocker);

        _storage.Checkpoints.Clear();
        _disks.Disks[DiskPath] = new VirtualDiskInfo(64 * Gb, true, @"D:\VMs\Dev\Base.vhdx");
        Assert.Equal(DiskGrowBlocker.Differencing, (await Single()).GrowBlocker);

        _disks.Disks.Remove(DiskPath);
        Assert.Equal(DiskGrowBlocker.NotFound, (await Single()).GrowBlocker);
    }

    [Fact]
    public async Task OffVm_GrowsTheDisk_AndExtendsThePartitionOffline()
    {
        var job = await GrowAsync(new GrowVmDiskRequest(DiskPath, 128));

        var result = await FinishedAsync(job);
        Assert.Equal([(DiskPath, 128 * Gb)], _disks.Resized);
        Assert.Equal([DiskPath], _extender.Offline);
        Assert.Equal(new DiskGrowResult(DiskPath, 128 * Gb, PartitionExtended: true, Note: null), result.DiskGrowResult);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task RunningVm_GrowsInPlace_AndExtendsInTheGuest_WithTheStoredAdministrator()
    {
        _inventory.SetState(VmId, VmState.Running);
        _credentials.Save(VmId, new GuestCredential("hhadmin", "secret"));

        var result = await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));

        Assert.Equal([(VmId, 100 * Gb)], _extender.InGuest);
        Assert.Empty(_extender.Offline);
        Assert.True(result.DiskGrowResult!.PartitionExtended);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task RunningVm_WithoutACredential_GrowsTheDisk_AndSaysToExtendItInTheVm()
    {
        _inventory.SetState(VmId, VmState.Running);

        var result = await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));

        Assert.Single(_disks.Resized);
        Assert.False(result.DiskGrowResult!.PartitionExtended);
        Assert.Contains("Disk Management", result.DiskGrowResult.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxGuest_GetsTheCommands_InsteadOfAnExtension()
    {
        _inventory.Vms[0] = FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off, GuestOsFamily.Linux);

        var result = await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));

        Assert.Empty(_extender.Offline);
        Assert.Equal(VmDiskService.LinuxNote, result.DiskGrowResult!.Note);
    }

    [Fact]
    public async Task ExtendPartitionFalse_OnlyGrowsTheDisk()
    {
        var result = await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100, ExtendPartition: false)));

        Assert.Empty(_extender.Offline);
        Assert.False(result.DiskGrowResult!.PartitionExtended);
        Assert.Null(result.DiskGrowResult.Note);
    }

    [Fact]
    public async Task RunningVhd_NeedsTheVmOff_UnlessAskedToShutDown()
    {
        _inventory.SetState(VmId, VmState.Running);
        _disks.Disks[DiskPath] = new VirtualDiskInfo(64 * Gb, IsVhdx: false, ParentPath: null);

        var refused = await Assert.ThrowsAsync<LifecycleConflictException>(() => GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));
        Assert.Equal(ContractInfo.ProblemCodes.RequiresShutdown, refused.Code);

        var result = await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100, ShutDownToApply: true)));
        Assert.Equal([VmAction.Shutdown, VmAction.Start], _power.Calls);
        Assert.Equal([DiskPath], _extender.Offline);
        Assert.True(result.DiskGrowResult!.PartitionExtended);
    }

    [Theory]
    [InlineData(64)]
    [InlineData(32)]
    [InlineData(70000)]
    public async Task ASizeThatIsNotLarger_IsAValidationError(int sizeGb)
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => GrowAsync(new GrowVmDiskRequest(DiskPath, sizeGb)));

        Assert.Equal("sizeGb", Assert.Single(error.Errors).Field);
        Assert.Empty(_disks.Resized);
    }

    [Fact]
    public async Task APathThatIsNotTheVmsDisk_IsAValidationError()
    {
        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => GrowAsync(new GrowVmDiskRequest(@"D:\Other.vhdx", 100)));

        Assert.Equal("path", Assert.Single(error.Errors).Field);
    }

    [Fact]
    public async Task Checkpoints_AreRefused()
    {
        _storage.Checkpoints[VmId] = 2;

        var error = await Assert.ThrowsAsync<LifecycleConflictException>(() => GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));

        Assert.Contains("Merge its checkpoints", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADriveWithoutRoom_IsAWarning_UntilAcknowledged()
    {
        _files.FreeSpaceMb = 10 * 1024;

        await Assert.ThrowsAsync<ResourceWarningsException>(() => GrowAsync(new GrowVmDiskRequest(DiskPath, 100)));
        await FinishedAsync(await GrowAsync(new GrowVmDiskRequest(DiskPath, 100, AcknowledgeWarnings: true)));

        Assert.Single(_disks.Resized);
    }

    private async Task<VmDisk> Single() => Assert.Single((await _service.ListAsync(VmId, CancellationToken.None)).Disks);

    private Task<VmJobSnapshot> GrowAsync(GrowVmDiskRequest request) => _service.GrowAsync(VmId, UserId, request, null, CancellationToken.None);

    private async Task<VmJobSnapshot> FinishedAsync(VmJobSnapshot job)
    {
        Assert.Equal(VmJobKind.GrowDisk, job.Kind);
        await _jobs.WhenFinished(job.Id);
        var finished = _jobs.Get(job.Id, UserId)!;
        Assert.True(finished.State == VmJobState.Succeeded, finished.ErrorDetail);
        return finished;
    }

    private sealed class FakeDisks : IHyperVDisks
    {
        public Dictionary<string, VirtualDiskInfo> Disks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<(string Path, long Size)> Resized { get; } = [];

        public Task<VirtualDiskInfo?> ReadAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(Disks.TryGetValue(path, out var info) ? info : null);

        public Task ResizeAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken)
        {
            Resized.Add((path, sizeBytes));
            progress(100);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeExtender : IPartitionExtender
    {
        public List<string> Offline { get; } = [];

        public List<(Guid VmId, long Size)> InGuest { get; } = [];

        public Task<PartitionExtension> ExtendOfflineAsync(string path, CancellationToken cancellationToken)
        {
            Offline.Add(path);
            return Task.FromResult(new PartitionExtension(true, null));
        }

        public Task<PartitionExtension> ExtendInGuestAsync(Guid vmId, GuestCredential admin, long sizeBytes, CancellationToken cancellationToken)
        {
            InGuest.Add((vmId, sizeBytes));
            return Task.FromResult(new PartitionExtension(true, null));
        }
    }

    private sealed class ShutdownPower(FakeVmInventory inventory) : IHyperVPowerInvoker
    {
        public List<VmAction> Calls { get; } = [];

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Calls.Add(action);
            inventory.SetState(vmId, action == VmAction.Shutdown ? VmState.Off : VmState.Running);
            return Task.CompletedTask;
        }
    }
}
