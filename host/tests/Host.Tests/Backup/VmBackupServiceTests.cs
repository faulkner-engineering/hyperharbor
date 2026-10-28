using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

internal sealed class FakeCheckpoints : IHyperVCheckpoints
{
    public List<CheckpointInfo> Items { get; } = [];

    /// <summary>Every call in order, for example "create:Production" and "remove".</summary>
    public List<string> Log { get; }

    public HashSet<CheckpointKind> FailCreate { get; } = [];

    public bool FailRemove { get; set; }

    /// <summary>Hyper-V made the checkpoint, then reporting it (renaming it) failed.</summary>
    public bool ThrowAfterCreate { get; set; }

    public FakeCheckpoints(List<string> log) => Log = log;

    public Task<IReadOnlyList<CheckpointInfo>> ListAsync(Guid vmId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CheckpointInfo>>(Items.ToList());

    public Task<CheckpointInfo> CreateAsync(Guid vmId, string name, CheckpointKind kind, CancellationToken cancellationToken)
    {
        Log.Add($"create:{kind}");
        if (FailCreate.Contains(kind))
        {
            throw new CheckpointException($"no {kind} checkpoint");
        }

        var info = new CheckpointInfo(Guid.NewGuid().ToString("N"), name, null);
        Items.Add(info);
        if (ThrowAfterCreate)
        {
            throw new HyperVJobFailedException("rename", 1, "boom");
        }

        return Task.FromResult(info);
    }

    public Task RemoveAsync(Guid vmId, string checkpointId, CancellationToken cancellationToken)
    {
        Log.Add("remove");
        if (FailRemove)
        {
            throw new HyperVJobFailedException("DestroySnapshot", 1, "merge failed");
        }

        Items.RemoveAll(c => c.Id == checkpointId);
        return Task.CompletedTask;
    }
}

internal sealed class FakeDefinitions : IHyperVDefinitionReader
{
    public Func<VmDefinition> Current { get; set; } = null!;

    public Exception? ThrowOnRead { get; set; }

    public Task<VmDefinition> ReadAsync(Guid vmId, CancellationToken cancellationToken) =>
        ThrowOnRead is not null ? throw ThrowOnRead : Task.FromResult(Current());
}

internal sealed class FakeStaging : IBackupStaging
{
    public List<string> Log { get; }

    public Dictionary<string, string> Links { get; } = [];

    public Dictionary<string, byte[]> Files { get; } = [];

    public bool FailLink { get; set; }

    public FakeStaging(List<string> log) => Log = log;

    public void Prepare(string folder) => Log.Add("staging:prepare");

    public void Link(string existingFile, string linkPath)
    {
        if (FailLink)
        {
            throw new BackupException("different volume");
        }

        Links[linkPath] = existingFile;
    }

    public long Length(string path) => 1000;

    public void WriteFile(string path, byte[] contents) => Files[path] = contents;

    public void Remove(string folder) => Log.Add("staging:remove");
}

internal sealed class FakeRepositories(string dataDirectory) : IBackupRepositories
{
    public int Opened { get; private set; }

    public Exception? Throw { get; set; }

    public Task<OpenRepository> OpenAsync(BackupTarget target, RepositoryRole role, CancellationToken cancellationToken)
    {
        Opened++;
        if (Throw is not null)
        {
            throw Throw;
        }

        return Task.FromResult(new OpenRepository(
            KopiaBackendFactory.For(target, role, "repo1", dataDirectory, new MemorySecrets()), "passphrase"));
    }
}

public sealed class VmBackupServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly List<string> _log = [];
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeCheckpoints _checkpoints;
    private readonly FakeDefinitions _definitions = new();
    private readonly FakeStaging _staging;
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakeRepositories _repositories;
    private readonly FakeKopiaRunner _kopia = new();
    private readonly BackupHistoryStore _history;
    private readonly PerformanceStore _performance;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero));
    private readonly BackupTarget _target = new() { Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\Backups") };

    public VmBackupServiceTests()
    {
        _checkpoints = new FakeCheckpoints(_log);
        _staging = new FakeStaging(_log);
        _repositories = new FakeRepositories(_directory);
        _history = new BackupHistoryStore(_directory);
        _performance = new PerformanceStore(_directory);
        _definitions.Current = () => Definition();
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Running));
        _kopia.Respond = call =>
        {
            _log.Add($"kopia:{call.Arguments[0]} {call.Arguments[1]}");
            return call.Arguments is ["snapshot", "create", ..]
                ? new KopiaResult(0, """{ "id": "snap1", "source": { "path": "p" }, "startTime": "2026-10-12T02:00:00Z", "stats": { "totalSize": 4242 } }""", string.Empty)
                : new KopiaResult(0, string.Empty, string.Empty);
        };
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static VmDefinition Definition(bool gpu = false, params string[] disks) => new(
        VmId, "Web", 2, "hello", new ComputeState(2, 2048, 4096, true, false, false, 1), true, "1734c6e8-3154-4dda-ba5f-a874cc483422", true,
        [new VmAdapterDefinition("sw1", "External", "00155D010203")],
        (disks.Length == 0 ? [@"D:\VMs\Web\Web.vhdx"] : disks).Select((d, i) => new VmDiskDefinition(d, "scsi", 0, i)).ToList(),
        gpu ? new GpuPartitionDefinition(1, 2, 3, 4) : null, 128, 512);

    private VmBackupService Service() => new(
        _inventory, _definitions, _checkpoints, _storage, _staging, _repositories, new KopiaCli(_kopia), _history, _performance,
        _ => true, _time, NullLogger<VmBackupService>.Instance, "1.2.3");

    private VmBackupRequest Request(GpuLiveCopy gpu = GpuLiveCopy.RequireOff) =>
        new(VmId, _target, new BackupPolicy { VmId = VmId.ToString("D"), TargetId = "t1", GpuLiveCopy = gpu, Retention = new RetentionPolicy(7, 4, 6) }, BackupTrigger.Manual);

    private Task<BackupRunRecord> RunAsync(VmBackupRequest? request = null, CancellationToken ct = default) =>
        Service().RunAsync(request ?? Request(), (_, _) => { }, ct);

    [Fact]
    public async Task RunningVm_IsBackedUpFromAProductionCheckpoint_ThatIsRemovedAfterwards()
    {
        _definitions.Current = () => _checkpoints.Items.Count > 0
            ? Definition(false, @"D:\VMs\Web\Web_AUTO.avhdx")
            : Definition();
        _storage.Parents[@"D:\VMs\Web\Web_AUTO.avhdx"] = @"D:\VMs\Web\Web.vhdx";

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.Equal("snap1", record.SnapshotId);
        Assert.Equal(4242, record.TotalBytes);
        Assert.Empty(_checkpoints.Items);
        Assert.Equal(@"D:\VMs\Web\Web.vhdx", Assert.Single(_staging.Links.Values));
        Assert.EndsWith("disk-0.vhdx", Assert.Single(_staging.Links.Keys));
        Assert.Equal(["create:Production"], _log.Where(l => l.StartsWith("create")));
        Assert.Equal(record.RunId, _history.List()[0].RunId);
    }

    [Fact]
    public async Task Cleanup_RemovesTheStagingFolderBeforeTheCheckpoint()
    {
        await RunAsync();

        var staging = _log.IndexOf("staging:remove");
        var checkpoint = _log.IndexOf("remove");
        Assert.True(staging >= 0 && checkpoint > staging, string.Join(", ", _log));
    }

    [Fact]
    public async Task WhenTheUploadFails_TheCheckpointIsStillRemoved_AndTheRunFails()
    {
        _kopia.Respond = call => call.Arguments is ["snapshot", "create", ..]
            ? new KopiaResult(1, string.Empty, "dial tcp: no such host")
            : new KopiaResult(0, string.Empty, string.Empty);

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Empty(_checkpoints.Items);
        Assert.Contains("remove", _log);
        Assert.Contains("staging:remove", _log);
        Assert.Contains("kopia failed", record.Reason);
    }

    [Fact]
    public async Task WhenLinkingTheDisksFails_TheCheckpointIsStillRemoved()
    {
        _staging.FailLink = true;

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Contains("different volume", record.Reason);
        Assert.Empty(_checkpoints.Items);
    }

    [Fact]
    public async Task WhenReadingTheSettingsFailsAfterTheCheckpoint_TheCheckpointIsStillRemoved()
    {
        _definitions.Current = () => _checkpoints.Items.Count > 0 ? throw new IOException("read failed") : Definition();

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Empty(_checkpoints.Items);
    }

    [Fact]
    public async Task WhenCancelled_TheCheckpointIsStillRemoved()
    {
        using var cancel = new CancellationTokenSource();
        _kopia.Respond = call =>
        {
            if (call.Arguments is ["snapshot", "create", ..])
            {
                cancel.Cancel();
                throw new OperationCanceledException(cancel.Token);
            }

            return new KopiaResult(0, string.Empty, string.Empty);
        };

        var record = await RunAsync(ct: cancel.Token);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Equal("Cancelled", record.Reason);
        Assert.Empty(_checkpoints.Items);
    }

    [Fact]
    public async Task WhenTheCheckpointCannotBeRemoved_TheBackupIsPartial_AndTheNextRunRemovesIt()
    {
        _checkpoints.FailRemove = true;
        var first = await RunAsync();
        _checkpoints.FailRemove = false;

        Assert.Equal(BackupOutcome.Partial, first.Outcome);
        Assert.Equal("snap1", first.SnapshotId);
        Assert.Contains(first.Warnings, w => w.Contains("could not be removed"));
        var stale = Assert.Single(_checkpoints.Items);
        Assert.True(stale.IsHyperHarborBackup);

        _log.Clear();
        var second = await RunAsync();

        Assert.Equal(BackupOutcome.Succeeded, second.Outcome);
        Assert.Contains(second.Warnings, w => w.Contains("left by an earlier backup"));
        Assert.Empty(_checkpoints.Items);
    }

    [Fact]
    public async Task WhenAProductionCheckpointIsUnavailable_AStandardOneIsUsed_AndTheManifestSaysSo()
    {
        _checkpoints.FailCreate.Add(CheckpointKind.Production);

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.Equal(["create:Production", "create:Standard"], _log.Where(l => l.StartsWith("create")));
        Assert.Contains(record.Warnings, w => w.Contains("crash consistent"));
        var manifest = BackupManifest.Parse(_staging.Files.Single(f => f.Key.EndsWith("manifest.json")).Value);
        Assert.Equal(ConsistencyMode.Standard, manifest.Consistency.Mode);
    }

    [Fact]
    public async Task WhenCreatingTheCheckpointFailsAfterHyperVMadeIt_ItIsStillRemoved()
    {
        _checkpoints.ThrowAfterCreate = true;

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Empty(_checkpoints.Items);
        Assert.Contains("remove", _log);
    }

    [Fact]
    public async Task WhenNoCheckpointCanBeTaken_NothingIsLeftBehind()
    {
        _checkpoints.FailCreate.Add(CheckpointKind.Production);
        _checkpoints.FailCreate.Add(CheckpointKind.Standard);

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Empty(_checkpoints.Items);
        Assert.DoesNotContain("remove", _log);
    }

    [Fact]
    public async Task OffVm_IsCopiedDirectly_WithoutACheckpoint()
    {
        _inventory.SetState(VmId, VmState.Off);

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.DoesNotContain(_log, l => l.StartsWith("create"));
        var manifest = BackupManifest.Parse(_staging.Files.Single(f => f.Key.EndsWith("manifest.json")).Value);
        Assert.Equal(ConsistencyMode.Off, manifest.Consistency.Mode);
    }

    [Fact]
    public async Task VmWithCheckpointsOfItsOwn_IsSkippedWithAWarning_AndNothingIsUploaded()
    {
        _checkpoints.Items.Add(new CheckpointInfo("c1", "Before upgrade", null));

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Skipped, record.Outcome);
        Assert.Equal("ExistingCheckpoints", record.Reason);
        Assert.Contains("checkpoints of its own", record.Warnings[0]);
        Assert.Single(_checkpoints.Items);
        Assert.DoesNotContain(_log, l => l.StartsWith("kopia"));
        Assert.Equal(0, _repositories.Opened);
    }

    [Fact]
    public async Task GpuVm_IsSkippedWhileRunning_ByDefault_AndCopiedLiveWhenAllowed()
    {
        _definitions.Current = () => Definition(gpu: true);

        var skipped = await RunAsync();
        var live = await RunAsync(Request(GpuLiveCopy.AllowLive));

        Assert.Equal(BackupOutcome.Skipped, skipped.Outcome);
        Assert.Equal("NeedsOff", skipped.Reason);
        Assert.Equal(BackupOutcome.Succeeded, live.Outcome);
        Assert.Contains(live.Warnings, w => w.Contains("crash consistent"));
        Assert.DoesNotContain(_log, l => l.StartsWith("create"));
        var manifest = BackupManifest.Parse(_staging.Files.Single(f => f.Key.EndsWith("manifest.json")).Value);
        Assert.Equal(ConsistencyMode.Live, manifest.Consistency.Mode);
        Assert.Equal(new ManifestGpu(1, 2, 3, 4, 128, 512), manifest.Gpu);
    }

    [Fact]
    public async Task UnconfirmedRepository_FailsTheRunBeforeAnyCheckpoint()
    {
        _repositories.Throw = new BackupException("recovery key has not been confirmed");

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.Contains("recovery key", record.Reason);
        Assert.DoesNotContain(_log, l => l.StartsWith("create"));
    }

    [Fact]
    public async Task RetentionAndBandwidth_AreAppliedBeforeTheSnapshot()
    {
        var request = Request() with { Policy = Request().Policy with { Retention = new RetentionPolicy(14, 2, 12), BandwidthKbps = 8000 } };

        await RunAsync(request);

        Assert.Contains(_kopia.Calls, c => c.Arguments.Contains("--keep-daily=14") && c.Arguments.Contains("--keep-monthly=12"));
        Assert.Contains(_kopia.Calls, c => c.Arguments.Contains("--upload-bytes-per-second=1000000"));
        var order = _kopia.Calls.Select(c => c.Arguments[1]).ToList();
        Assert.True(order.IndexOf("set") < order.IndexOf("create"), string.Join(",", order));
    }

    [Fact]
    public async Task Manifest_CarriesTheVmDefinition_Notes_AndPerformanceState()
    {
        _performance.Save(new PerformanceRecord(VmId, new PerformanceSettings(4, 8192), _time.GetUtcNow(),
            new GuestDriverRecord(GpuVendor.Intel, "31.0.101", ["x"], _time.GetUtcNow(), false)));

        await RunAsync();

        var manifest = BackupManifest.Parse(_staging.Files.Single(f => f.Key.EndsWith("manifest.json")).Value);
        Assert.Equal("Web", manifest.Vm.Name);
        Assert.Equal("hello", manifest.Vm.Notes);
        Assert.Equal(2, manifest.Vm.Generation);
        Assert.Equal(new ManifestCompute(2, 2048, 4096, true, false, false), manifest.Compute);
        Assert.Equal(new ManifestFirmware(true, "1734c6e8-3154-4dda-ba5f-a874cc483422"), manifest.Firmware);
        Assert.True(manifest.Tpm);
        Assert.Equal("External", manifest.Network[0].SwitchName);
        Assert.Equal(new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 1000), manifest.Disks[0]);
        Assert.True(manifest.Performance.Applied);
        Assert.Equal("31.0.101", manifest.Performance.GuestDriverVersion);
        Assert.True(manifest.Guest.Provisioned);
        Assert.Equal("1.2.3", manifest.HyperHarborVersion);
    }

    [Fact]
    public void Manifest_FromANewerSchema_IsRefused_AndUnknownFieldsAreIgnored()
    {
        var ok = BackupManifest.Parse(System.Text.Encoding.UTF8.GetBytes("""
            { "schemaVersion": 1, "hyperHarborVersion": "1", "createdAt": "2026-10-12T00:00:00Z", "future": 5,
              "vm": { "id": "11111111-2222-3333-4444-555555555555", "name": "x", "generation": 2 },
              "compute": { "vCpu": 1, "startupMemoryMb": 512, "dynamicMemory": false, "nestedVirtualization": false, "macAddressSpoofing": false },
              "firmware": { "secureBoot": true }, "performance": { "applied": false },
              "consistency": { "mode": "off", "warnings": [] }, "guest": { "provisioned": false } }
            """));
        Assert.Equal("x", ok.Vm.Name);

        var error = Assert.Throws<InvalidDataException>(() => BackupManifest.Parse("""{ "schemaVersion": 2 }"""u8));
        Assert.Contains("newer", error.Message);
    }

    [Theory]
    [InlineData(VmState.Off, 0, false, ConsistencyAction.DirectCopy)]
    [InlineData(VmState.Running, 0, false, ConsistencyAction.Checkpoint)]
    [InlineData(VmState.Running, 1, false, ConsistencyAction.Skip)]
    [InlineData(VmState.Off, 1, false, ConsistencyAction.Skip)]
    [InlineData(VmState.Off, 0, true, ConsistencyAction.Skip)]
    [InlineData(VmState.Saved, 0, false, ConsistencyAction.Skip)]
    [InlineData(VmState.Paused, 0, false, ConsistencyAction.Skip)]
    [InlineData(VmState.Starting, 0, false, ConsistencyAction.Skip)]
    public void Planner_DecidesFromStateAndCheckpoints(VmState state, int foreign, bool differencing, ConsistencyAction expected) =>
        Assert.Equal(expected, BackupConsistencyPlanner.Plan(new ConsistencyInputs(state, false, GpuLiveCopy.RequireOff, foreign, differencing)).Action);

    [Fact]
    public async Task History_KeepsTheNewestRecords_AndReportsTheLastSuccess()
    {
        await RunAsync();
        _inventory.SetState(VmId, VmState.Paused);
        await RunAsync();

        var list = _history.List();
        Assert.Equal(BackupOutcome.Skipped, list[0].Outcome);
        Assert.Equal(BackupOutcome.Succeeded, _history.LastSuccess($"vm:{VmId:D}", "t1")!.Outcome);
        Assert.Null(_history.LastSuccess($"vm:{VmId:D}", "other"));
    }

    [Fact]
    public async Task DiskSources_UseTheFixedSplitter_AndTheRunRecordsWhatItUploaded()
    {
        var stored = 100L;
        _kopia.Respond = call => call.Arguments switch
        {
            ["content", "stats", ..] => new KopiaResult(0, $"Total Bytes: {stored}\n", string.Empty),
            ["snapshot", "create", ..] => Snapshot(4242, () => stored += 300),
            _ => new KopiaResult(0, string.Empty, string.Empty),
        };

        var record = await RunAsync();

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.Equal(300, record.UploadedBytes);
        Assert.Equal(300, _history.List()[0].UploadedBytes);
        Assert.Contains("--splitter=FIXED-1M", _kopia.Calls.Single(c => c.Arguments is ["policy", "set", ..]).Arguments);
        Assert.DoesNotContain(_kopia.Calls, c => c.Arguments is ["policy", "show", ..]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("FIXED-1M", false)]
    public async Task AFullUpload_AfterAnEarlierBackup_IsExplained(string? splitterBefore, bool layoutChanged)
    {
        await RunAsync();
        var stored = 0L;
        _kopia.Respond = call => call.Arguments switch
        {
            ["policy", "show", ..] => new KopiaResult(0, splitterBefore is null ? """{ "splitter": {} }""" : $$"""{ "splitter": { "algorithm": "{{splitterBefore}}" } }""", string.Empty),
            ["content", "stats", ..] => new KopiaResult(0, $"Total Bytes: {stored}\n", string.Empty),
            ["snapshot", "create", ..] => Snapshot(10L << 30, () => stored += 9L << 30),
            _ => new KopiaResult(0, string.Empty, string.Empty),
        };

        var record = await RunAsync();

        var warning = Assert.Single(record.Warnings);
        Assert.Contains("9.0 GB of 10.0 GB", warning);
        Assert.Equal(layoutChanged, warning.Contains("new layout"));
        Assert.Equal(!layoutChanged, warning.Contains("BitLocker"));
    }

    [Theory]
    [InlineData(10L << 30, 9L << 30, true, true)]
    [InlineData(10L << 30, 7L << 30, true, false)]
    [InlineData(10L << 30, 9L << 30, false, false)]
    [InlineData(512L << 20, 500L << 20, true, false)]
    public void UploadWarning_OnlyForANearlyFullCopyOfALargeEnoughVm(long total, long uploaded, bool earlier, bool warns)
    {
        Assert.Equal(warns, VmBackupService.UploadWarning(total, uploaded, earlier, splitterChanged: false) is not null);
        Assert.Null(VmBackupService.UploadWarning(total, null, earlier, splitterChanged: false));
    }

    private static KopiaResult Snapshot(long totalSize, Action onCreate)
    {
        onCreate();
        return new KopiaResult(0, $$"""{ "id": "snap1", "source": { "path": "p" }, "startTime": "2026-10-12T02:00:00Z", "stats": { "totalSize": {{totalSize}} } }""", string.Empty);
    }
}
