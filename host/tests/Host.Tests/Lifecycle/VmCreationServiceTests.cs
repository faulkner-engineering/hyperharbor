using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmCreationServiceTests : IDisposable
{
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _isoFolder = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVBuilder _builder = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeHyperVHost _hyperV = new();
    private readonly FakeHostCapacity _capacity = new();
    private readonly FakeDiskFiles _files = new();
    private readonly LifecycleOptions _options = new();
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private VmCreationService _creation;

    public VmCreationServiceTests()
    {
        Directory.CreateDirectory(_isoFolder);
        File.WriteAllBytes(Path.Combine(_isoFolder, "win11.iso"), new byte[1]);
        _builder.OnDiskCreated = path => _files.Files.Add(path);
        _creation = Create();
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_isoFolder, recursive: true);
    }

    [Fact]
    public async Task Create_RunsEveryStep_WithTheDefaults()
    {
        var job = await _creation.StartAsync(UserId, Request(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        var done = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Succeeded, done.State);
        Assert.Equal(_builder.CreatedVmId, done.VmId);
        Assert.Equal(["disk", "define", "configure", "tpm", "notes"], _builder.Steps);
        var blueprint = _builder.Configured!;
        Assert.Equal("Win11 Dev", blueprint.Name);
        Assert.Equal(Path.Combine(_isoFolder, "win11.iso"), blueprint.IsoPath);
        Assert.Equal(@"C:\Hyper-V\Virtual Hard Disks\Win11 Dev.vhdx", blueprint.DiskPath);
        Assert.Null(blueprint.ConfigurationFolder);
        Assert.Equal(CimHyperVHost.DefaultSwitchId, blueprint.SwitchId);
        Assert.Equal(4096, blueprint.MaximumMemoryMb);
    }

    [Fact]
    public async Task Create_WithoutTpm_SkipsThatStep()
    {
        var job = await _creation.StartAsync(UserId, Request() with { EnableTpm = false }, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Assert.DoesNotContain("tpm", _builder.Steps);
    }

    [Fact]
    public async Task ConfiguredVmRoot_HoldsTheVmAndItsDisk()
    {
        _options.VmRootFolder = @"D:\VMs";
        _creation = Create();

        var blueprint = await _creation.PlanAsync(Request(), CancellationToken.None);

        Assert.Equal(@"D:\VMs\Win11 Dev", blueprint.ConfigurationFolder);
        Assert.Equal(@"D:\VMs\Win11 Dev\Virtual Hard Disks\Win11 Dev.vhdx", blueprint.DiskPath);
    }

    [Theory]
    [InlineData("", "name")]
    [InlineData("bad:name", "name")]
    [InlineData("trailing.", "name")]
    [InlineData("CON", "name")]
    [InlineData("com1", "name")]
    public async Task InvalidNames_AreRefused(string name, string field)
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => _creation.StartAsync(UserId, Request() with { Name = name }, null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == field);
        Assert.Empty(_builder.Steps);
    }

    [Fact]
    public async Task ExistingName_IsRefused_RegardlessOfCase()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(Guid.NewGuid(), "WIN11 DEV", VmState.Off));

        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => _creation.StartAsync(UserId, Request(), null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == "name" && error.Message.Contains("already exists", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingDiskFile_IsNeverOverwritten()
    {
        _files.Files.Add(@"C:\Hyper-V\Virtual Hard Disks\Win11 Dev.vhdx");

        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => _creation.StartAsync(UserId, Request(), null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == "name");
    }

    [Fact]
    public async Task IsoOutsideTheLibrary_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(UserId, Request() with { IsoName = @"..\..\Windows\System32\x.iso" }, null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == "isoName");
    }

    [Theory]
    [InlineData(0, 4096, 4096, false, "processorCount")]
    [InlineData(17, 4096, 4096, false, "processorCount")]
    [InlineData(2, 4095, 4095, false, "startupMemoryMb")]
    [InlineData(2, 16, 16, false, "startupMemoryMb")]
    [InlineData(2, 40000, 40000, false, "startupMemoryMb")]
    [InlineData(2, 4096, 2048, true, "maximumMemoryMb")]
    public async Task SettingsBeyondTheHost_AreRefused(int processors, long startup, long maximum, bool dynamic, string field)
    {
        var request = Request() with { ProcessorCount = processors, StartupMemoryMb = startup, MaximumMemoryMb = maximum, DynamicMemory = dynamic };

        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => _creation.StartAsync(UserId, request, null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == field);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task DiskSizeOutOfRange_IsRefused(int sizeGb)
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(UserId, Request() with { DiskSizeGb = sizeGb }, null, CancellationToken.None));

        Assert.Contains(ex.Errors, error => error.Field == "diskSizeGb");
    }

    [Fact]
    public async Task StaticMemory_IgnoresTheMaximum()
    {
        var blueprint = await _creation.PlanAsync(Request() with { DynamicMemory = false, MaximumMemoryMb = 2 }, CancellationToken.None);

        Assert.Equal(4096, blueprint.MaximumMemoryMb);
    }

    [Fact]
    public async Task LeavingLessThanTheReserve_IsAWarning_UntilAcknowledged()
    {
        _capacity.Capacity = new HostCapacity(16, 32768, 6000);

        var warned = await Assert.ThrowsAsync<ResourceWarningsException>(() => _creation.StartAsync(UserId, Request(), null, CancellationToken.None));
        Assert.Equal("startupMemoryMb", Assert.Single(warned.Warnings).Field);
        Assert.Empty(_builder.Steps);

        var job = await _creation.StartAsync(UserId, Request() with { AcknowledgeWarnings = true }, null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);
        Assert.Equal(VmJobState.Succeeded, _jobs.Get(job.Id, UserId)!.State);
    }

    [Fact]
    public async Task DiskLargerThanTheFreeSpace_IsAWarning()
    {
        _files.FreeSpaceMb = 10 * 1024;

        var warned = await Assert.ThrowsAsync<ResourceWarningsException>(() => _creation.StartAsync(UserId, Request(), null, CancellationToken.None));

        Assert.Equal("diskSizeGb", Assert.Single(warned.Warnings).Field);
    }

    [Fact]
    public async Task ReserveIsConfigurable()
    {
        _capacity.Capacity = new HostCapacity(16, 32768, 6000);
        _options.HostMemoryReserveMb = 1024;
        _creation = Create();

        Assert.NotNull(await _creation.PlanAsync(Request(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownSwitch_IsRefused_AndNoDefaultSwitch_IsAWarning()
    {
        var unknown = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            _creation.StartAsync(UserId, Request() with { SwitchId = "nope" }, null, CancellationToken.None));
        Assert.Contains(unknown.Errors, error => error.Field == "switchId");

        _hyperV.Switches.RemoveAll(item => item.IsDefault);
        var warned = await Assert.ThrowsAsync<ResourceWarningsException>(() => _creation.StartAsync(UserId, Request(), null, CancellationToken.None));
        Assert.Contains(warned.Warnings, warning => warning.Field == "switchId");
    }

    [Fact]
    public async Task ChosenSwitch_IsUsed()
    {
        var blueprint = await _creation.PlanAsync(Request() with { SwitchId = "1D6E5B3C-0000-4000-8000-000000000001" }, CancellationToken.None);

        Assert.Equal("1d6e5b3c-0000-4000-8000-000000000001", blueprint.SwitchId);
    }

    [Theory]
    [InlineData("define")]
    [InlineData("configure")]
    [InlineData("tpm")]
    public async Task FailureAfterTheDiskIsCreated_RemovesTheDisk_AndAnyVm(string failAt)
    {
        _builder.FailAt = failAt;

        var job = await _creation.StartAsync(UserId, Request(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        var failed = _jobs.Get(job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.Contains("Simulated failure", failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal([@"C:\Hyper-V\Virtual Hard Disks\Win11 Dev.vhdx"], _files.Deleted);
        Assert.Equal(failAt == "define" ? [] : [_builder.CreatedVmId], _storage.DeletedVms);
    }

    [Fact]
    public async Task FailureCreatingTheDisk_DeletesNothing()
    {
        _builder.FailAt = "disk";

        var job = await _creation.StartAsync(UserId, Request(), null, CancellationToken.None);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Failed, _jobs.Get(job.Id, UserId)!.State);
        Assert.Empty(_files.Deleted);
        Assert.Empty(_storage.DeletedVms);
    }

    [Fact]
    public async Task SameNameTwice_WhileTheFirstIsRunning_IsAConflict()
    {
        var release = new TaskCompletionSource();
        var slow = new SlowBuilder(_builder, release.Task);
        var creation = Create(slow);

        var first = await creation.StartAsync(UserId, Request(), null, CancellationToken.None);
        await Assert.ThrowsAsync<LifecycleConflictException>(() => creation.StartAsync(UserId, Request(), null, CancellationToken.None));

        release.SetResult();
        await _jobs.WhenFinished(first.Id);
    }

    private static CreateVmRequest Request() =>
        new(" Win11 Dev ", "win11.iso", 64, 4, 4096, 8192, DynamicMemory: false);

    private VmCreationService Create(IHyperVBuilder? builder = null) => new(
        _inventory,
        builder ?? _builder,
        _storage,
        _hyperV,
        _capacity,
        _files,
        new IsoLibrary(_isoFolder),
        _jobs,
        _options,
        NullLogger<VmCreationService>.Instance);

    /// <summary>Waits before creating the disk, so a second request arrives while the first job runs.</summary>
    private sealed class SlowBuilder(IHyperVBuilder inner, Task release) : IHyperVBuilder
    {
        public async Task CreateDiskAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken)
        {
            await release;
            await inner.CreateDiskAsync(path, sizeBytes, progress, cancellationToken);
        }

        public Task<Guid> DefineAsync(VmBlueprint blueprint, string notes, CancellationToken cancellationToken) => inner.DefineAsync(blueprint, notes, cancellationToken);

        public Task ConfigureAsync(Guid vmId, VmBlueprint blueprint, CancellationToken cancellationToken) => inner.ConfigureAsync(vmId, blueprint, cancellationToken);

        public Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken) => inner.EnableTpmAsync(vmId, cancellationToken);

        public Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken) => inner.SetNotesAsync(vmId, notes, cancellationToken);
    }
}
