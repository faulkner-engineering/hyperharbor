using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Host.Tests.Performance;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

internal sealed class FakeImporter : IHyperVImporter
{
    public Guid NewId { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");

    public ImportPlan? Plan { get; private set; }

    public int TpmCalls { get; private set; }

    public string? FailAt { get; set; }

    public Task<Guid> CreateAsync(ImportPlan plan, CancellationToken cancellationToken)
    {
        if (FailAt == "create")
        {
            throw new IOException("create failed");
        }

        Plan = plan;
        return Task.FromResult(NewId);
    }

    public Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken)
    {
        TpmCalls++;
        return FailAt == "tpm" ? throw new IOException("tpm failed") : Task.CompletedTask;
    }
}

/// <summary>Writes the manifest and the disk files a real restore would produce.</summary>
internal sealed class FakeRestorer : ISnapshotRestorer
{
    public BackupManifest? Manifest { get; set; }

    public byte[]? RawManifest { get; set; }

    public Dictionary<string, byte[]> Extra { get; } = [];

    public bool SkipDisks { get; set; }

    public Task RestoreAsync(OpenRepository repository, string snapshotId, string destination, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destination);
        if (RawManifest is not null || Manifest is not null)
        {
            File.WriteAllBytes(Path.Combine(destination, BackupManifest.FileName), RawManifest ?? Manifest!.ToJson());
        }

        if (Manifest is not null && !SkipDisks)
        {
            foreach (var disk in Manifest.Disks)
            {
                File.WriteAllBytes(Path.Combine(destination, disk.File), [1, 2, 3]);
            }
        }

        foreach (var (name, bytes) in Extra)
        {
            var path = Path.Combine(destination, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeHeartbeat : IGuestHeartbeat
{
    public bool Answers { get; set; } = true;

    public Task<bool> WaitAsync(Guid vmId, TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(Answers);
}

internal sealed class FakeProvisioner : IRestoreProvisioner
{
    public int Calls { get; private set; }

    public Task ProvisionAsync(Guid vmId, ProvisionVmRequest administrator, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.CompletedTask;
    }
}

public sealed class RestoreNamingAndMappingTests
{
    [Fact]
    public void A_free_name_is_kept_and_collisions_get_numbered_suffixes_without_regard_to_case()
    {
        Assert.Equal("Dev", RestoreNaming.UniqueName("Dev", ["Other"]));
        Assert.Equal("Dev (restored)", RestoreNaming.UniqueName("Dev", ["dev"]));
        Assert.Equal("Dev (restored 2)", RestoreNaming.UniqueName("Dev", ["Dev", "DEV (restored)"]));
        Assert.Equal("Dev (restored 3)", RestoreNaming.UniqueName("Dev", ["Dev", "Dev (restored)", "dev (restored 2)"]));
    }

    private static readonly VirtualSwitch Default = new("c08cb7b8", "Default Switch", true);
    private static readonly VirtualSwitch External = new("ext", "External", false);

    [Fact]
    public void An_adapter_goes_to_the_switch_of_the_same_name()
    {
        var result = SwitchMapper.Map([new ManifestAdapter("external", "old", null)], [Default, External]);

        Assert.Equal("ext", Assert.Single(result.Adapters).SwitchId);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_missing_switch_falls_back_to_the_default_switch_with_a_warning()
    {
        var result = SwitchMapper.Map([new ManifestAdapter("Lab", "old", null)], [Default, External]);

        Assert.Equal("c08cb7b8", Assert.Single(result.Adapters).SwitchId);
        Assert.Contains("Lab", Assert.Single(result.Warnings));
    }

    [Fact]
    public void Without_a_default_switch_the_adapter_stays_unconnected_with_a_warning()
    {
        var result = SwitchMapper.Map([new ManifestAdapter("Lab", "old", null)], [External]);

        Assert.Null(Assert.Single(result.Adapters).SwitchId);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void An_adapter_that_was_never_connected_stays_that_way_without_a_warning()
    {
        var result = SwitchMapper.Map([new ManifestAdapter(null, null, null)], [Default]);

        Assert.Null(Assert.Single(result.Adapters).SwitchId);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Snapshots_are_grouped_by_the_VM_id_in_their_source_folder_newest_first()
    {
        var one = Guid.Parse("11111111-0000-4000-8000-000000000001");
        var two = Guid.Parse("22222222-0000-4000-8000-000000000002");
        KopiaSnapshot Make(string id, Guid vm, int day) =>
            new(id, "h", "u", $@"D:\VMs\.hh-backup\{vm:N}", new DateTimeOffset(2026, 10, day, 0, 0, 0, TimeSpan.Zero), null, 1, null);

        var groups = SnapshotCatalog.GroupByVm([Make("a", one, 1), Make("b", one, 3), Make("c", two, 2),
            new KopiaSnapshot("x", "h", "u", @"C:\something else", DateTimeOffset.UnixEpoch, null, 0, null)]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(["b", "a"], groups.Single(g => g.VmId == one).Snapshots.Select(s => s.Id));
        Assert.Equal(["c"], groups.Single(g => g.VmId == two).Snapshots.Select(s => s.Id));
    }
}

public sealed class RestoreCoordinatorTests : IDisposable
{
    private static readonly Guid OriginalId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVHost _host = new();
    private readonly FakeImporter _importer = new();
    private readonly FakeHyperVCompute _compute = new();
    private readonly FakeHyperVPerformance _performance = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly FakePowerInvoker _power = new();
    private readonly FakeHeartbeat _heartbeat = new();
    private readonly FakeProvisioner _provisioner = new();
    private readonly FakeRestorer _restorer = new();
    private readonly FakeRepositories _repositories;
    private readonly PerformanceStore _performanceStore;
    private readonly BackupTarget _target = new() { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B") };
    private readonly string _destination;

    public RestoreCoordinatorTests()
    {
        _repositories = new FakeRepositories(_root);
        _performanceStore = new PerformanceStore(_root);
        _destination = Directory.CreateDirectory(Path.Combine(_root, "vms")).FullName;
        _restorer.Manifest = Manifest();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static BackupManifest Manifest(Action<ManifestBuilderOptions>? tweak = null)
    {
        var options = new ManifestBuilderOptions();
        tweak?.Invoke(options);
        return new BackupManifest
        {
            HyperHarborVersion = "1.0.0",
            CreatedAt = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero),
            Vm = new ManifestVm(OriginalId, "Dev", options.Generation, "notes"),
            Compute = new ManifestCompute(4, 4096, options.Dynamic ? 8192 : null, options.Dynamic, options.Nested, options.Spoofing),
            Firmware = new ManifestFirmware(true, "1734c6e8-3154-4dda-ba5f-a874cc483422"),
            Tpm = options.Tpm,
            Network = [new ManifestAdapter(options.Switch, "old-id", null)],
            Disks = [new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 3)],
            Gpu = options.Gpu ? new ManifestGpu(1, 2, 3, 4, 256, 1024) : null,
            Performance = new ManifestPerformance(options.Gpu, null, null),
            Consistency = new ManifestConsistency(ConsistencyMode.Production, []),
            Guest = new ManifestGuest("Windows", options.Provisioned),
        };
    }

    private sealed class ManifestBuilderOptions
    {
        public int Generation { get; set; } = 2;

        public bool Dynamic { get; set; }

        public bool Nested { get; set; }

        public bool Spoofing { get; set; }

        public bool Tpm { get; set; }

        public bool Gpu { get; set; }

        public bool Provisioned { get; set; }

        public string Switch { get; set; } = "External";
    }

    private RestoreCoordinator Coordinator() => new(
        _repositories, _restorer, _inventory, _host, _importer, _compute, _performance, _storage, _power, _heartbeat, _provisioner,
        _performanceStore, new FakeTimeProvider(), NullLogger<RestoreCoordinator>.Instance);

    private RestoreVmRequest Request(bool start = false, ProvisionVmRequest? admin = null, string? name = null) =>
        new(_target, "snap1", _destination, name, start, admin);

    private Task<RestoreVmResult> RunAsync(RestoreVmRequest? request = null) =>
        Coordinator().RestoreAsync(request ?? Request(), (_, _) => { }, default);

    [Fact]
    public async Task Restores_the_disks_and_defines_the_VM_from_the_manifest_on_a_new_id()
    {
        _restorer.Manifest = Manifest(o => { o.Dynamic = true; });

        var result = await RunAsync();

        Assert.Equal(RestoreOutcome.Succeeded, result.Outcome);
        Assert.Equal(_importer.NewId, result.VmId);
        Assert.NotEqual(OriginalId, result.VmId);
        var plan = _importer.Plan!;
        Assert.Equal("Dev", plan.Name);
        Assert.Equal(4, plan.ProcessorCount);
        Assert.Equal(4096, plan.StartupMemoryMb);
        Assert.Equal(8192, plan.MaximumMemoryMb);
        Assert.True(plan.DynamicMemory);
        Assert.Equal("notes", plan.Notes);
        Assert.Equal("1d6e5b3c-0000-4000-8000-000000000001", Assert.Single(plan.Adapters).SwitchId);
        var disk = Assert.Single(plan.Disks);
        Assert.Equal(Path.Combine(_destination, "Dev", "Virtual Hard Disks", "disk-0.vhdx"), disk.Path);
        Assert.True(File.Exists(disk.Path));
        Assert.DoesNotContain(Directory.EnumerateDirectories(_destination), d => Path.GetFileName(d).StartsWith(".hh-restore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_name_collision_with_a_VM_or_a_folder_adds_a_suffix_and_a_warning()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(Guid.NewGuid(), "Dev", VmState.Off));
        Directory.CreateDirectory(Path.Combine(_destination, "Dev (restored)"));

        var result = await RunAsync();

        Assert.Equal("Dev (restored 2)", result.VmName);
        Assert.Equal(RestoreOutcome.SucceededWithWarnings, result.Outcome);
        Assert.Contains(result.Warnings, w => w.Contains("Dev (restored 2)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_explicit_name_is_used_for_the_VM_and_its_folder()
    {
        var result = await RunAsync(Request(name: "Dev copy"));

        Assert.Equal("Dev copy", result.VmName);
        Assert.True(Directory.Exists(Path.Combine(_destination, "Dev copy")));
    }

    [Fact]
    public async Task A_missing_switch_falls_back_to_the_default_switch()
    {
        _restorer.Manifest = Manifest(o => { o.Switch = "Gone"; });

        var result = await RunAsync();

        Assert.Equal(CimHyperVHost.DefaultSwitchId, Assert.Single(_importer.Plan!.Adapters).SwitchId);
        Assert.Contains(result.Warnings, w => w.Contains("Gone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nested_virtualization_and_mac_spoofing_are_applied_after_the_VM_exists()
    {
        _restorer.Manifest = Manifest(o => { o.Nested = true; o.Spoofing = true; });

        await RunAsync();

        var (vmId, change) = Assert.Single(_compute.Applied);
        Assert.Equal(_importer.NewId, vmId);
        Assert.True(change.NestedVirtualization);
        Assert.True(change.MacAddressSpoofing);
    }

    [Fact]
    public async Task A_VM_with_a_TPM_gets_a_new_key_protector_and_a_BitLocker_warning()
    {
        _restorer.Manifest = Manifest(o => { o.Tpm = true; });

        var result = await RunAsync();

        Assert.Equal(1, _importer.TpmCalls);
        Assert.True(result.BitLockerRecoveryMayBeNeeded);
        Assert.Contains(result.Warnings, w => w.Contains("BitLocker", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_VM_without_a_TPM_gets_none()
    {
        var result = await RunAsync();

        Assert.Equal(0, _importer.TpmCalls);
        Assert.False(result.BitLockerRecoveryMayBeNeeded);
    }

    [Fact]
    public async Task A_GPU_partition_is_restored_and_flagged_for_a_driver_sync()
    {
        _restorer.Manifest = Manifest(o => { o.Gpu = true; });

        var result = await RunAsync();

        Assert.True(result.NeedsDriverResync);
        var plan = _performance.Applied[_importer.NewId];
        Assert.Equal((1UL, 2UL, 3UL, 4UL), (plan.Gpu.Vram, plan.Gpu.Encode, plan.Gpu.Decode, plan.Gpu.Compute));
        Assert.Equal(256, plan.LowMmioGapMb);
        Assert.Contains(result.Warnings, w => w.Contains("Performance mode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Generation_1_backup_is_refused_before_anything_is_created()
    {
        _restorer.Manifest = Manifest(o => { o.Generation = 1; });

        var error = await Assert.ThrowsAsync<RestoreException>(() => RunAsync());

        Assert.Contains("Generation 1", error.Message, StringComparison.Ordinal);
        Assert.Null(_importer.Plan);
        Assert.Empty(Directory.GetFileSystemEntries(_destination));
    }

    [Fact]
    public async Task A_newer_manifest_is_refused_with_an_update_hint()
    {
        _restorer.Manifest = null;
        _restorer.RawManifest = """{ "schemaVersion": 99 }"""u8.ToArray();

        var error = await Assert.ThrowsAsync<RestoreException>(() => RunAsync());

        Assert.Contains("newer HyperHarbor", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_snapshot_without_a_manifest_is_refused()
    {
        _restorer.Manifest = null;
        _restorer.RawManifest = null;

        await Assert.ThrowsAsync<RestoreException>(() => RunAsync());
        Assert.Empty(Directory.GetFileSystemEntries(_destination));
    }

    [Fact]
    public async Task A_failure_defining_the_VM_removes_the_restored_files()
    {
        _importer.FailAt = "create";

        await Assert.ThrowsAsync<IOException>(() => RunAsync());

        Assert.Empty(Directory.GetFileSystemEntries(_destination));
        Assert.Empty(_storage.DeletedVms);
    }

    [Fact]
    public async Task A_failure_after_the_VM_exists_deletes_that_VM_and_its_files()
    {
        _restorer.Manifest = Manifest(o => { o.Tpm = true; });
        _importer.FailAt = "tpm";

        await Assert.ThrowsAsync<IOException>(() => RunAsync());

        Assert.Equal([_importer.NewId], _storage.DeletedVms);
        Assert.Empty(Directory.GetFileSystemEntries(_destination));
    }

    [Fact]
    public async Task A_backup_error_from_the_repository_surfaces_as_a_restore_error()
    {
        _repositories.Throw = new BackupException("recovery key has not been confirmed");

        var error = await Assert.ThrowsAsync<RestoreException>(() => RunAsync());

        Assert.Contains("recovery key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Starting_waits_for_the_guest_and_provisions_once_with_the_administrator_credential()
    {
        _restorer.Manifest = Manifest(o => { o.Provisioned = true; });
        var admin = new ProvisionVmRequest("admin", "pw");

        var result = await RunAsync(Request(start: true, admin: admin));

        Assert.Equal(RestoreOutcome.Succeeded, result.Outcome);
        Assert.Equal([(_importer.NewId, VmAction.Start)], _power.Calls);
        Assert.Equal(1, _provisioner.Calls);
    }

    [Fact]
    public async Task A_VM_that_was_not_provisioned_is_not_provisioned_after_restore()
    {
        await RunAsync(Request(start: true, admin: new ProvisionVmRequest("admin", "pw")));

        Assert.Equal(0, _provisioner.Calls);
    }

    [Fact]
    public async Task No_heartbeat_keeps_the_VM_and_asks_for_the_console()
    {
        _heartbeat.Answers = false;

        var result = await RunAsync(Request(start: true));

        Assert.Equal(RestoreOutcome.NeedsConsole, result.Outcome);
        Assert.Empty(_storage.DeletedVms);
        Assert.Contains(result.Warnings, w => w.Contains("console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_start_that_fails_keeps_the_restored_VM_with_a_warning()
    {
        _power.ThrowOnInvoke = new VmActionNotAllowedException(VmAction.Start, VmState.Off, "not enough memory");

        var result = await RunAsync(Request(start: true));

        Assert.Equal(RestoreOutcome.SucceededWithWarnings, result.Outcome);
        Assert.Empty(_storage.DeletedVms);
        Assert.Contains(result.Warnings, w => w.Contains("could not be started", StringComparison.Ordinal));
    }
}

public sealed class HostStateRestorerTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeRestorer _restorer = new();
    private readonly BackupTarget _target = new() { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B") };

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private HostStateRestorer Restorer() => new(_data, new FakeRepositories(_data), _restorer, NullLogger<HostStateRestorer>.Instance);

    private void Snapshot(int format, params string[] files)
    {
        var marker = new HostStateMarker(format, "1.0.0", "PC", DateTimeOffset.UnixEpoch, files);
        _restorer.Extra[HostStateMarker.FileName] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(marker, BackupJson.Options);
        foreach (var file in files)
        {
            _restorer.Extra[file] = System.Text.Encoding.UTF8.GetBytes($"restored:{file}");
        }
    }

    [Fact]
    public async Task Writes_the_allowlisted_files_and_profiles_and_asks_for_a_restart()
    {
        Snapshot(DataFormat.Current, "paired-devices.json", "host-settings.json", "profiles/user1/gaming.yaml");

        var result = await Restorer().RestoreAsync(_target, "s", (_, _) => { }, default);

        Assert.Equal(3, result.Restored.Count);
        Assert.True(result.RestartRequired);
        Assert.Equal("restored:paired-devices.json", File.ReadAllText(Path.Combine(_data, "paired-devices.json")));
        Assert.Equal("restored:profiles/user1/gaming.yaml", File.ReadAllText(Path.Combine(_data, "profiles", "user1", "gaming.yaml")));
        Assert.Contains(result.Warnings, w => w.Contains("Provision", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("profiles/../../evil.json")]
    [InlineData("C:/evil.json")]
    [InlineData("vm-credentials.json.protected")]
    [InlineData("backup-config.json")]
    [InlineData("random.txt")]
    public async Task Names_outside_the_allowlist_are_skipped_and_never_written(string name)
    {
        Snapshot(DataFormat.Current, "host-settings.json");
        var marker = new HostStateMarker(DataFormat.Current, "1.0.0", "PC", DateTimeOffset.UnixEpoch, ["host-settings.json", name]);
        _restorer.Extra[HostStateMarker.FileName] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(marker, BackupJson.Options);

        var result = await Restorer().RestoreAsync(_target, "s", (_, _) => { }, default);

        Assert.Equal(["host-settings.json"], result.Restored);
        Assert.Contains(name, result.Skipped);
        Assert.False(File.Exists(Path.Combine(_data, "evil.json")));
        Assert.False(File.Exists(Path.Combine(_data, name.Replace('/', Path.DirectorySeparatorChar))) && name != "host-settings.json");
    }

    [Fact]
    public async Task A_backup_of_a_newer_data_format_is_refused_and_writes_nothing()
    {
        Snapshot(DataFormat.Current + 1, "host-settings.json");

        var error = await Assert.ThrowsAsync<RestoreException>(() => Restorer().RestoreAsync(_target, "s", (_, _) => { }, default));

        Assert.Contains("data format", error.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_data, "host-settings.json")));
    }

    [Fact]
    public async Task A_snapshot_without_a_marker_is_refused()
    {
        await Assert.ThrowsAsync<RestoreException>(() => Restorer().RestoreAsync(_target, "s", (_, _) => { }, default));
    }
}

public sealed class DiskReplacerTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000c");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeVmInventory _inventory = new();
    private readonly FakeRestorer _restorer = new();
    private readonly FakeCheckpoints _checkpoints = new([]);
    private readonly FakeDefinitions _definitions = new();
    private readonly string _disk;
    private readonly BackupTarget _target = new() { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B") };

    public DiskReplacerTests()
    {
        _disk = Path.Combine(_root, "dev.vhdx");
        File.WriteAllBytes(_disk, [9, 9, 9, 9]);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _definitions.Current = () => new VmDefinition(
            VmId, "Dev", 2, null, new ComputeState(2, 2048, 2048, false, false, false, 1), true, null, false, [],
            [new VmDiskDefinition(_disk, "scsi", 0, 0)], null, 128, 512);
        _restorer.Manifest = new BackupManifest
        {
            HyperHarborVersion = "1", CreatedAt = DateTimeOffset.UnixEpoch,
            Vm = new ManifestVm(VmId, "Dev", 2, null),
            Compute = new ManifestCompute(2, 2048, null, false, false, false),
            Firmware = new ManifestFirmware(true, null),
            Disks = [new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 3)],
            Performance = new ManifestPerformance(false, null, null),
            Consistency = new ManifestConsistency(ConsistencyMode.Off, []),
            Guest = new ManifestGuest(null, false),
        };
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Task ReplaceAsync(string typed = "Dev") =>
        new DiskReplacer(new FakeRepositories(_root), _restorer, _inventory, _definitions, _checkpoints, NullLogger<DiskReplacer>.Instance)
            .ReplaceAsync(new ReplaceDiskRequest(VmId, typed, _target, "s"), (_, _) => { }, default);

    [Fact]
    public async Task Swaps_in_the_restored_disk_and_removes_the_old_one()
    {
        await ReplaceAsync();

        Assert.Equal<byte[]>([1, 2, 3], File.ReadAllBytes(_disk));
        Assert.False(File.Exists(_disk + DiskReplacer.PreRestoreSuffix));
        Assert.Equal([_disk], Directory.GetFiles(_root));
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("")]
    [InlineData("Dev ")]
    public async Task The_typed_name_must_match_exactly(string typed)
    {
        await Assert.ThrowsAsync<RestoreException>(() => ReplaceAsync(typed));

        Assert.Equal<byte[]>([9, 9, 9, 9], File.ReadAllBytes(_disk));
    }

    [Fact]
    public async Task A_running_VM_is_refused()
    {
        _inventory.SetState(VmId, VmState.Running);

        await Assert.ThrowsAsync<RestoreException>(() => ReplaceAsync());

        Assert.Equal<byte[]>([9, 9, 9, 9], File.ReadAllBytes(_disk));
    }

    [Fact]
    public async Task A_VM_with_checkpoints_is_refused()
    {
        _checkpoints.Items.Add(new CheckpointInfo("c", "mine", null));

        await Assert.ThrowsAsync<RestoreException>(() => ReplaceAsync());
    }

    [Fact]
    public async Task A_backup_with_a_different_number_of_disks_is_refused()
    {
        _restorer.Manifest = _restorer.Manifest! with { Disks = [.. _restorer.Manifest.Disks, new ManifestDisk("disk-1.vhdx", "scsi", 0, 1, 3)] };

        await Assert.ThrowsAsync<RestoreException>(() => ReplaceAsync());

        Assert.Equal<byte[]>([9, 9, 9, 9], File.ReadAllBytes(_disk));
    }

    [Fact]
    public async Task A_missing_restored_disk_leaves_the_original_untouched()
    {
        _restorer.SkipDisks = true;

        await Assert.ThrowsAsync<RestoreException>(() => ReplaceAsync());

        Assert.Equal<byte[]>([9, 9, 9, 9], File.ReadAllBytes(_disk));
        Assert.Empty(Directory.GetDirectories(_root));
    }
}
