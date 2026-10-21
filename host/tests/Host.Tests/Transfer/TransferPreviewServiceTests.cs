using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Transfer;

public sealed class TransferPreviewServiceTests : IDisposable
{
    private const string Disk = @"D:\VMs\Web\Virtual Hard Disks\Web.vhdx";
    private const string DataDisk = @"D:\VMs\Web\Virtual Hard Disks\Data.vhdx";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid OtherVmId = Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab");

    private readonly string _data = Path.Combine(Path.GetTempPath(), "hyperharbor-transfer-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeDefinitions _definitions = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeDiskFiles _files = new();
    private readonly FakeHyperVVersions _versions = new();
    private readonly UnattendedInstallStore _installs;
    private readonly TransferPreviewService _service;

    public TransferPreviewServiceTests()
    {
        Directory.CreateDirectory(_data);
        _installs = new UnattendedInstallStore(_data);
        _service = new TransferPreviewService(_inventory, _definitions, _storage, _files, _versions, _installs);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
        _definitions.Current = () => Definition(Disk, DataDisk);
        _storage.Attach(VmId, "Web", Disk);
        _storage.Attach(VmId, "Web", DataDisk);
        _files.Files.UnionWith([Disk, DataDisk]);
        _files.Sizes[Disk] = 20L << 30;
        _files.Sizes[DataDisk] = 5L << 30;
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    [Fact]
    public async Task OffVm_WithItsOwnDisks_HasNoBlockers_AndReportsWhatTheTargetNeeds()
    {
        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Empty(preview.Blockers);
        Assert.Empty(preview.Warnings);
        Assert.Equal("Web", preview.Name);
        Assert.Equal("12.0", preview.ConfigurationVersion);
        Assert.Equal(2, preview.Generation);
        Assert.True(preview.TpmEnabled);
        Assert.Equal([Disk, DataDisk], preview.Disks.Select(disk => disk.Path));
        Assert.Equal(25L << 30, preview.TotalBytes);
        Assert.Equal("00155D010203", Assert.Single(preview.Adapters).MacAddress);
    }

    [Theory]
    [InlineData(FakeKeyProtectorAccess.LocalId, true)]
    [InlineData("CCCC", false)]
    public async Task ATpm_ReportsWhoHoldsItsKey_AndWhetherThisHostOwnsIt(string owner, bool ownedHere)
    {
        var keys = new FakeKeyProtectorAccess { Holders = new(owner, ["DDDD"]) };
        var service = new TransferPreviewService(_inventory, _definitions, _storage, _files, _versions, _installs, keys: keys);

        var preview = await service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Equal(new KeyProtectorHolders(owner, ["DDDD"]).Owner, preview.TpmKeyHolders!.Owner);
        Assert.Equal(["DDDD"], preview.TpmKeyHolders.Guardians);
        Assert.Equal(ownedHere, preview.TpmOwnedBySource);
    }

    [Theory]
    [InlineData(VmState.Running, TransferBlockerCode.NotOff)]
    [InlineData(VmState.Paused, TransferBlockerCode.NotOff)]
    [InlineData(VmState.Saved, TransferBlockerCode.SavedState)]
    public async Task VmThatIsNotOff_IsBlocked(VmState state, TransferBlockerCode expected)
    {
        _inventory.SetState(VmId, state);

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(preview.Blockers).Code);
    }

    [Fact]
    public async Task Checkpoints_BlockOnlyAsCheckpoints_NotAsDifferencingDisks()
    {
        const string current = @"D:\VMs\Web\Virtual Hard Disks\Web_ABC.avhdx";
        _definitions.Current = () => Definition(current);
        _storage.Disks.Clear();
        _storage.Attach(VmId, "Web", current);
        _storage.Attach(VmId, "Web", Disk, inCheckpoint: true);
        _storage.Checkpoints[VmId] = 2;
        _storage.Parents[current] = Disk;
        _files.Files.Add(current);

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        var blocker = Assert.Single(preview.Blockers);
        Assert.Equal(TransferBlockerCode.Checkpoints, blocker.Code);
        Assert.Equal(2, preview.CheckpointCount);
    }

    [Fact]
    public async Task DifferencingDiskOnATemplate_IsBlocked()
    {
        _storage.Parents[Disk] = @"C:\Templates\Windows11.vhdx";

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        var blocker = Assert.Single(preview.Blockers);
        Assert.Equal(TransferBlockerCode.DifferencingDisk, blocker.Code);
        Assert.Contains("Windows11.vhdx", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiskAlsoAttachedToAnotherVm_IsBlocked()
    {
        _storage.Attach(OtherVmId, "Other", DataDisk.ToUpperInvariant());

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        var blocker = Assert.Single(preview.Blockers);
        Assert.Equal(TransferBlockerCode.SharedDisk, blocker.Code);
        Assert.Contains("Other", blocker.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDisk_IsBlocked_AndLeftOutOfTheDisks()
    {
        _files.Files.Remove(DataDisk);

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Equal(TransferBlockerCode.DiskMissing, Assert.Single(preview.Blockers).Code);
        Assert.Equal([Disk], preview.Disks.Select(disk => disk.Path));
    }

    [Fact]
    public async Task RunningUnattendedInstall_IsBlocked_AndAFinishedOneIsNot()
    {
        var now = DateTimeOffset.UtcNow;
        var install = new UnattendedInstall(VmId, Guid.NewGuid(), "windows-workstation", InstallOs.Windows, false, "seed.iso",
            UnattendedInstallState.Installing, "Installing", now, now);
        _installs.Save(install);

        var running = await _service.PreviewAsync(VmId, CancellationToken.None);
        _installs.Save(install with { State = UnattendedInstallState.Ready });
        var finished = await _service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Equal(TransferBlockerCode.InstallInProgress, Assert.Single(running.Blockers).Code);
        Assert.Empty(finished.Blockers);
    }

    [Fact]
    public async Task GpuPartition_AndAttachedImage_AreWarnings()
    {
        _definitions.Current = () => Definition(Disk, DataDisk) with { Gpu = new GpuPartitionDefinition(1, 2, 3, 4) };
        _storage.Images.Add(new DiskAttachment(VmId, "Web", @"D:\ISOs\Windows.iso", false));

        var preview = await _service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Empty(preview.Blockers);
        Assert.True(preview.HasGpuPartition);
        Assert.Equal(2, preview.Warnings.Count);
    }

    [Fact]
    public async Task ABackupSchedule_IsAWarning()
    {
        var backups = new BackupConfigStore(_data);
        backups.Update(config => config with { Policies = [new BackupPolicy { VmId = VmId.ToString("D"), TargetId = "t1" }] });
        var service = new TransferPreviewService(_inventory, _definitions, _storage, _files, _versions, _installs, backups);

        var preview = await service.PreviewAsync(VmId, CancellationToken.None);

        Assert.Empty(preview.Blockers);
        Assert.Contains("backup", Assert.Single(preview.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnknownVm_ThrowsNotFound()
    {
        await Assert.ThrowsAsync<VmNotFoundException>(() => _service.PreviewAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static VmDefinition Definition(params string[] disks) => new(
        VmId, "Web", 2, null, new ComputeState(4, 4096, 8192, true, false, false, 1), true, CimHyperVBuilder.MicrosoftWindowsTemplateId, true,
        [new VmAdapterDefinition("sw1", "External", "00155D010203")],
        disks.Select((disk, i) => new VmDiskDefinition(disk, "scsi", 0, i)).ToList(),
        null, 128, 512);
}
