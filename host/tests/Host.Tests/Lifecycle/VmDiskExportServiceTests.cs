using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmDiskExportServiceTests : IDisposable
{
    private const string BaseDisk = @"C:\VMs\Dev\Dev.vhdx";
    private const string DataDisk = @"D:\Data\Dev.vhdx";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hh-export-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsDirectory = Path.Combine(Path.GetTempPath(), "hh-settings-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakeDiskCopier _copier = new();
    private readonly HostSettingsStore _settings;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 4, 18, 30, 0, TimeSpan.Zero));
    private readonly VmJobStore _jobs = new(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
    private readonly VmDiskExportService _export;

    public VmDiskExportServiceTests()
    {
        Directory.CreateDirectory(_settingsDirectory);
        _settings = new HostSettingsStore(_settingsDirectory);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _export = new VmDiskExportService(_inventory, _storage, _files, _copier, new BackupLocation(_settings), _jobs, _time, NullLogger<VmDiskExportService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _storage.Attach(VmId, "Dev", BaseDisk);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        foreach (var directory in new[] { _root, _settingsDirectory })
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(VmState.Running)]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    public async Task Export_RefusesAVmThatIsNotOff(VmState state)
    {
        _inventory.SetState(VmId, state);

        var ex = await Assert.ThrowsAsync<LifecycleConflictException>(() => _export.StartAsync(VmId, UserId, _root, null, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.VmMustBeOff, ex.Code);
        Assert.Empty(_copier.Copies);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task Export_CopiesEveryDiskIntoATimestampedFolder_AndNeverAnIso()
    {
        _storage.Attach(VmId, "Dev", DataDisk);
        _storage.Images.Add(new DiskAttachment(VmId, "Dev", @"C:\ISOs\Win11.iso", false));

        var job = await RunAsync(_root);

        Assert.Equal(VmJobState.Succeeded, job.State);
        var folder = Path.Combine(_root, "Dev-20261004-1830");
        Assert.Equal(
            [(BaseDisk, Path.Combine(folder, "Dev.vhdx")), (DataDisk, Path.Combine(folder, "Dev-1.vhdx"))],
            _copier.Copies);
    }

    [Fact]
    public async Task Export_OfADiskWithCheckpoints_CopiesTheWholeChain_BaseFirst()
    {
        const string checkpoint = @"C:\VMs\Dev\Dev_6A1F.avhdx";
        _storage.Disks.Clear();
        _storage.Attach(VmId, "Dev", checkpoint);
        _storage.Attach(VmId, "Dev", BaseDisk, inCheckpoint: true);
        _storage.Parents[checkpoint] = BaseDisk;

        await RunAsync(_root);

        Assert.Equal([BaseDisk, checkpoint], _copier.Copies.Select(copy => copy.Source));
    }

    [Fact]
    public async Task Export_WithoutAFolder_UsesTheBackupFolderFromTheTray()
    {
        _settings.SetBackupFolder(_root);

        await RunAsync(null);

        Assert.StartsWith(Path.Combine(_root, "Dev-"), Assert.Single(_copier.Copies).Destination, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_AddsANumber_WhenTheFolderExists()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Dev-20261004-1830"));

        await RunAsync(_root);

        Assert.Equal(Path.Combine(_root, "Dev-20261004-1830-2", "Dev.vhdx"), Assert.Single(_copier.Copies).Destination);
    }

    [Fact]
    public async Task Export_RefusesARelativeFolder_AndAVolumeWithoutSpace()
    {
        var invalid = await Assert.ThrowsAsync<LifecycleValidationException>(() => _export.StartAsync(VmId, UserId, @"Backups\Dev", null, CancellationToken.None));
        Assert.Equal("destinationFolder", Assert.Single(invalid.Errors).Field);

        _files.FreeSpaceMb = 512;
        var full = await Assert.ThrowsAsync<LifecycleConflictException>(() => _export.StartAsync(VmId, UserId, _root, null, CancellationToken.None));
        Assert.Contains("needs 1 GB", full.Message, StringComparison.Ordinal);
        Assert.Empty(_copier.Copies);
    }

    [Fact]
    public async Task Export_ThatFails_RemovesTheIncompleteFolder()
    {
        _storage.Attach(VmId, "Dev", DataDisk);
        _copier.FailOn = DataDisk;

        var job = await RunAsync(_root);

        Assert.Equal(VmJobState.Failed, job.State);
        Assert.Equal("There is not enough space on the disk.", job.ErrorDetail);
        Assert.Empty(Directory.GetDirectories(_root));
    }

    private async Task<VmJobSnapshot> RunAsync(string? destination)
    {
        var finished = new TaskCompletionSource<VmJobSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _export.StartAsync(VmId, UserId, destination, finished.SetResult, CancellationToken.None);
        return await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
