using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Installation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class HostStateBackupServiceTests : IDisposable
{
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeKopiaRunner _kopia = new();
    private readonly FakeRepositories _repositories;
    private readonly BackupHistoryStore _history;
    private readonly BackupTarget _target = new() { Id = "t1", Name = "L", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B") };
    private readonly List<string> _snapshotted = [];

    public HostStateBackupServiceTests()
    {
        _repositories = new FakeRepositories(_data);
        _history = new BackupHistoryStore(_data);
        _kopia.Respond = call =>
        {
            if (call.Arguments is ["snapshot", "create", var path, ..])
            {
                _snapshotted.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(path, f).Replace('\\', '/')));
                return new KopiaResult(0, """{ "id": "h1", "source": { "path": "p" }, "stats": { "totalSize": 77 } }""", string.Empty);
            }

            return new KopiaResult(0, string.Empty, string.Empty);
        };
    }

    public void Dispose() => Directory.Delete(_data, recursive: true);

    private HostStateBackupService Service() =>
        new(_data, _repositories, new KopiaCli(_kopia), _history, new FakeTimeProvider(), NullLogger<HostStateBackupService>.Instance, "1.2.3");

    private void Seed()
    {
        foreach (var name in new[] { "paired-devices.json", "host-settings.json", "host-certificate.pfx.protected", "vm-credentials.json.protected",
                     "console-signin.json.protected", "backup-secrets.json.protected", "audit.log" })
        {
            File.WriteAllText(Path.Combine(_data, name), name);
        }

        Directory.CreateDirectory(Path.Combine(_data, "profiles", "user1"));
        File.WriteAllText(Path.Combine(_data, "profiles", "user1", "gaming.yaml"), "x");
    }

    [Fact]
    public async Task Backs_up_the_allowlisted_state_and_profiles_but_never_the_machine_bound_secrets()
    {
        Seed();

        var record = await Service().RunAsync(_target, BackupTrigger.Manual, (_, _) => { }, default);

        Assert.Equal(BackupOutcome.Succeeded, record.Outcome);
        Assert.Equal("h1", record.SnapshotId);
        Assert.Equal("host", record.Item);
        Assert.Contains("paired-devices.json", _snapshotted);
        Assert.Contains("host-certificate.pfx.protected", _snapshotted);
        Assert.Contains("profiles/user1/gaming.yaml", _snapshotted);
        Assert.Contains(HostStateMarker.FileName, _snapshotted);
        Assert.DoesNotContain("vm-credentials.json.protected", _snapshotted);
        Assert.DoesNotContain("console-signin.json.protected", _snapshotted);
        Assert.DoesNotContain("backup-secrets.json.protected", _snapshotted);
        Assert.DoesNotContain("audit.log", _snapshotted);
        Assert.False(Directory.Exists(Service().StagingFolder));
    }

    [Fact]
    public async Task The_marker_records_the_data_format_and_the_files()
    {
        Seed();
        var service = Service();
        var collected = service.Collect();

        var marker = System.Text.Json.JsonSerializer.Deserialize<HostStateMarker>(
            File.ReadAllBytes(Path.Combine(service.StagingFolder, HostStateMarker.FileName)), BackupJson.Options)!;

        Assert.Equal(DataFormat.Current, marker.DataFormat);
        Assert.Equal(collected, marker.Files);
        Assert.Equal("1.2.3", marker.HyperHarborVersion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_failed_upload_removes_the_staging_folder_and_records_the_failure()
    {
        Seed();
        _kopia.Respond = _ => new KopiaResult(1, string.Empty, "dial tcp: no such host");
        var service = Service();

        var record = await service.RunAsync(_target, BackupTrigger.Schedule, (_, _) => { }, default);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.False(Directory.Exists(service.StagingFolder));
        Assert.Equal(BackupTrigger.Schedule, _history.List()[0].Trigger);
    }

    [Fact]
    public async Task An_unconfirmed_repository_fails_before_anything_is_copied()
    {
        Seed();
        _repositories.Throw = new BackupException("recovery key has not been confirmed");
        var service = Service();

        var record = await service.RunAsync(_target, BackupTrigger.Manual, (_, _) => { }, default);

        Assert.Equal(BackupOutcome.Failed, record.Outcome);
        Assert.False(Directory.Exists(service.StagingFolder));
        Assert.Empty(_kopia.Calls);
    }
}
