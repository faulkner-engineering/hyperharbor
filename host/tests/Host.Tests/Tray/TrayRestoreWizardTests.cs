using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Host.Tests.Performance;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Tray;

public sealed class TrayRestoreWizardTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _vms;
    private readonly MemorySecrets _secrets = new();
    private readonly FakeKopiaRunner _kopia = new();
    private readonly BackupConfigStore _config;
    private readonly FakeRestorer _restorer = new();
    private readonly FakeImporter _importer = new();
    private readonly TrayRestoreWizard _wizard;
    private readonly List<string> _phases = [];

    public TrayRestoreWizardTests()
    {
        _vms = Directory.CreateDirectory(Path.Combine(_root, "vms")).FullName;
        _config = new BackupConfigStore(_root);
        var cli = new KopiaCli(_kopia);
        var repositories = new FakeRepositories(_root);
        var management = new BackupManagementService(
            _config, _secrets, new RecoveryKeyService(_secrets, new FakeTimeProvider()), new BackupTargetTester(cli, _secrets, _root),
            repositories, cli, _root);
        var host = new FakeHyperVHost { Defaults = new HyperVDefaults(_vms, _vms) };
        var coordinator = new RestoreCoordinator(
            repositories, _restorer, new FakeVmInventory(), host, _importer, new FakeHyperVCompute(), new FakeHyperVPerformance(),
            new FakeHyperVStorage(), new FakePowerInvoker(), new FakeHeartbeat(), new FakeProvisioner(), new PerformanceStore(_root),
            new FakeTimeProvider(), NullLogger<RestoreCoordinator>.Instance);
        var hostState = new HostStateRestorer(_root, repositories, _restorer, NullLogger<HostStateRestorer>.Instance);
        _wizard = new TrayRestoreWizard(
            management, coordinator, hostState, new VmStorageLocation(null, new LifecycleOptions(), host), host, NullLogger<TrayRestoreWizard>.Instance);
        _wizard.Changed += (_, _) => _phases.Add(_wizard.Status.Phase);
        _kopia.Respond = invocation => invocation.Arguments.Contains("list")
            ? new KopiaResult(0, $$"""
                [{ "id": "snap-vm", "source": { "path": "D:\\vms\\.hh-backup\\{{VmId:N}}" }, "description": "hyperharbor vm={{VmId}} name=Dev Box run=aa", "startTime": "2026-10-11T02:00:00Z" },
                 { "id": "snap-host", "source": { "path": "D:\\host" }, "startTime": "2026-10-11T02:00:00Z" }]
                """, string.Empty)
            : new KopiaResult(0, string.Empty, string.Empty);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static RestoreWizardForm LocalForm(string key = "AAAA-BBBB-CCCC") =>
        new("local", "Old backups", key, Path: @"D:\Backups");

    private async Task<RestoreWizardStatus> UntilAsync(Func<RestoreWizardStatus, bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            var status = _wizard.Status;
            if (done(status))
            {
                return status;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException(_wizard.Status.ToString());
    }

    private static BackupManifest Manifest() => new()
    {
        HyperHarborVersion = "1.0.0",
        CreatedAt = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero),
        Vm = new ManifestVm(VmId, "Dev Box", 2, null),
        Compute = new ManifestCompute(2, 2048, null, false, false, false),
        Firmware = new ManifestFirmware(true, null),
        Tpm = false,
        Network = [],
        Disks = [new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 3)],
        Gpu = null,
        Performance = new ManifestPerformance(false, null, null),
        Consistency = new ManifestConsistency(ConsistencyMode.Off, []),
        Guest = new ManifestGuest("Windows", false),
    };

    [Fact]
    public async Task Attach_ChecksTheKey_KeepsTheTarget_AndListsWhatItHolds()
    {
        _wizard.Attach(LocalForm());
        var status = await UntilAsync(s => s.Phase != "attaching" && s.Phase != "idle");

        Assert.Equal("ready", status.Phase);
        Assert.Equal("Old backups", status.TargetName);
        var vm = Assert.Single(status.Vms);
        Assert.Equal(VmId, vm.VmId);
        Assert.Equal("Dev Box", vm.Name);
        Assert.Equal("snap-vm", Assert.Single(vm.Snapshots).Id);
        Assert.Equal(2, status.HostSnapshots.Count);
        Assert.Equal(RepositoryKeyState.Confirmed, Assert.Single(_config.Load().Targets).VmRepository!.KeyState);

    }

    [Fact]
    public async Task AWrongKey_ReturnsToTheFormWithTheReason_AndKeepsNothing()
    {
        _kopia.Respond = _ => new KopiaResult(1, string.Empty, "ERROR invalid repository password");

        _wizard.Attach(LocalForm());
        var status = await UntilAsync(s => s.Error is not null);

        Assert.Equal("idle", status.Phase);
        Assert.NotEmpty(status.Error!);
        Assert.Empty(_config.Load().Targets);
        Assert.DoesNotContain("AAAA", status.Error);
    }

    [Fact]
    public async Task AttachingTheSameStorageTwice_ReusesTheTarget()
    {
        _wizard.Attach(LocalForm());
        await UntilAsync(s => s.Phase == "ready");
        _wizard.Reset();
        Assert.Equal("idle", _wizard.Status.Phase);

        _wizard.Attach(LocalForm());
        await UntilAsync(s => s.Phase == "ready");

        Assert.Single(_config.Load().Targets);
    }

    [Fact]
    public async Task RestoringAVm_CreatesItAsANewVm_AndReportsTheOutcome()
    {
        _restorer.Manifest = Manifest();
        _wizard.Attach(LocalForm());
        await UntilAsync(s => s.Phase == "ready");

        _wizard.Restore(new RestoreWizardRestoreMessage("vm", "snap-vm"));
        var status = await UntilAsync(s => s.Phase == "ready" && s.Messages.Count > 0);

        Assert.Null(status.Error);
        Assert.Contains("Dev Box was restored", status.Messages[0]);
        Assert.Equal("Dev Box", _importer.Plan!.Name);
        Assert.StartsWith(_vms, _importer.Plan.ConfigurationFolder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RestoringHostSettings_WritesThemAndAsksForARestart()
    {
        var marker = new HostStateMarker(DataFormat.Current, "1.0.0", "PC", DateTimeOffset.UnixEpoch, ["host-settings.json"]);
        _restorer.Extra[HostStateMarker.FileName] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(marker, BackupJson.Options);
        _restorer.Extra["host-settings.json"] = "{}"u8.ToArray();
        _wizard.Attach(LocalForm());
        await UntilAsync(s => s.Phase == "ready");

        _wizard.Restore(new RestoreWizardRestoreMessage("host", "snap-host"));
        var status = await UntilAsync(s => s.Phase == "ready" && s.Messages.Count > 0);

        Assert.True(status.RestartRequired);
        Assert.Contains("Host settings restored", status.Messages[0]);
        Assert.True(File.Exists(Path.Combine(_root, "host-settings.json")));
    }

    [Fact]
    public async Task AFailedRestore_ReturnsToReady_WithTheReason()
    {
        _restorer.Manifest = null;
        _wizard.Attach(LocalForm());
        await UntilAsync(s => s.Phase == "ready");

        _wizard.Restore(new RestoreWizardRestoreMessage("vm", "snap-vm"));
        var status = await UntilAsync(s => s.Phase == "ready" && s.Error is not null);

        Assert.Contains("manifest", status.Error);
    }

    [Fact]
    public void RestoreBeforeAttach_AsksToLookForBackupsFirst()
    {
        _wizard.Restore(new RestoreWizardRestoreMessage("vm", "snap-vm"));

        Assert.Equal("idle", _wizard.Status.Phase);
        Assert.Contains("Look for backups", _wizard.Status.Error);
    }

    [Fact]
    public void TheFormNeverPrintsItsSecrets()
    {
        var form = new RestoreWizardForm("s3", "Cloud", "SECRET-KEY-TEXT", AccessKey: "access-secret", SecretKey: "secret-secret");

        Assert.DoesNotContain("SECRET", form.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-secret", form.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SameStorage_ComparesTheLocationNotTheName()
    {
        var a = new BackupTarget { Id = "1", Name = "A", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\Backups\") };
        var b = new BackupTarget { Id = "2", Name = "B", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"d:\backups") };
        var s3 = new BackupTarget { Id = "3", Name = "C", Kind = BackupTargetKind.S3, S3 = new S3TargetSettings("s3.x.com", "bkt", null, "p", true, "", "") };
        var other = s3 with { S3 = s3.S3! with { Prefix = "q" } };

        Assert.True(TrayRestoreWizard.SameStorage(a, b));
        Assert.False(TrayRestoreWizard.SameStorage(a, s3));
        Assert.False(TrayRestoreWizard.SameStorage(s3, other));
    }
}
