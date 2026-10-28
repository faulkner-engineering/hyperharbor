using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Tests.Backup;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Runs the real kopia.exe against a temporary local folder: create, policy, snapshot, list, restore, wrong passphrase,
/// and a missing repository. An end-to-end test (scripts\e2e.ps1). Touches only temporary folders.
/// </summary>
[Trait("Category", "E2E")]
public sealed class KopiaLiveTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [E2EFact]
    public async Task Live_LocalRepository_RoundTripsASnapshot()
    {
        var cli = new KopiaCli(new KopiaProcessRunner(E2E.KopiaExe));
        var target = new BackupTarget
        {
            Id = "t1", Name = "Live", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_root, "dest")),
        };
        var repository = KopiaBackendFactory.For(target, RepositoryRole.Vm, "live1", _root, new MemorySecrets());
        const string passphrase = "live-test-passphrase";
        var source = Directory.CreateDirectory(Path.Combine(_root, "source")).FullName;
        File.WriteAllBytes(Path.Combine(source, "disk-0.vhdx"), new byte[3 * 1024 * 1024]);
        File.WriteAllText(Path.Combine(source, "manifest.json"), "{}");

        await cli.CreateRepositoryAsync(repository, passphrase, default);
        await cli.SetPolicyAsync(repository, passphrase, source, new RetentionPolicy(7, 4, 6), default);
        await cli.SetBandwidthAsync(repository, passphrase, 80_000, default);
        var created = await cli.CreateSnapshotAsync(repository, passphrase, source, "live test", default);
        var listed = await cli.ListSnapshotsAsync(repository, passphrase, default);

        var snapshot = Assert.Single(listed);
        Assert.Equal(created.Id, snapshot.Id);
        Assert.Equal(source, snapshot.SourcePath, ignoreCase: true);
        Assert.True(snapshot.TotalSize >= 3 * 1024 * 1024, $"total size {snapshot.TotalSize}");
        Assert.NotEqual(DateTimeOffset.MinValue, snapshot.StartTime);

        var restored = Path.Combine(_root, "restored");
        await cli.RestoreAsync(repository, passphrase, created.Id, restored, default);
        Assert.Equal(3 * 1024 * 1024, new FileInfo(Path.Combine(restored, "disk-0.vhdx")).Length);

        var wrong = await Assert.ThrowsAsync<KopiaException>(() => cli.ConnectAsync(repository, "wrong", readOnly: true, default));
        Assert.Equal(KopiaFailure.BadPassphrase, wrong.Failure);

        var empty = KopiaBackendFactory.For(
            target with { Local = new LocalTargetSettings(Path.Combine(_root, "empty")) }, RepositoryRole.Vm, "live2", _root, new MemorySecrets());
        Directory.CreateDirectory(Path.Combine(_root, "empty", "vm"));
        var missing = await Assert.ThrowsAsync<KopiaException>(() => cli.ConnectAsync(empty, passphrase, readOnly: true, default));
        Assert.Equal(KopiaFailure.NotARepository, missing.Failure);
    }
}
