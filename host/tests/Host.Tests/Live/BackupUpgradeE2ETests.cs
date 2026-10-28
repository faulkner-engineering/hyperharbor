using System.Globalization;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Updating a host from 0.2.4: its VM backups used kopia's default splitter and a history without the newer fields.
/// The first backup by this version must read that history, store the disk in the new layout (one full upload, with the
/// warning that says why), and the next backup must upload only what changed. Runs the real kopia.exe with Hyper-V
/// faked (the VM is off, so the disk is copied directly). An end-to-end test (scripts\e2e.ps1); writes about 1 GiB.
/// </summary>
[Trait("Category", "E2E")]
public sealed class BackupUpgradeE2ETests(ITestOutputHelper output) : IDisposable
{
    private const int DiskBytes = (1 << 30) + (1 << 20);
    private static readonly Guid VmId = Guid.Parse("5e5f2b10-9a1c-4d3e-8f70-2a4b6c8d0e1f");
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
    public async Task ABackupAfterUpdatingFrom024_UploadsTheDiskOnceInTheNewLayout_ThenOnlyChanges()
    {
        var disk = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "vms", "Web")).FullName, "Web.vhdx");
        var random = new Random(20261028);
        var data = new byte[DiskBytes];
        random.NextBytes(data);
        await File.WriteAllBytesAsync(disk, data);

        var dataDirectory = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var secrets = new MemorySecrets();
        secrets.Set(RecoveryKeyService.PassphraseReference("repo-upgrade"), "upgrade-passphrase");
        var target = new BackupTarget
        {
            Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_root, "dest")),
            VmRepository = new RepositoryRef("repo-upgrade", RepositoryKeyState.Confirmed),
        };
        var kopia = new KopiaCli(new KopiaProcessRunner(E2E.KopiaExe));
        var repositories = new BackupRepositories(kopia, secrets, dataDirectory);

        // 1. What 0.2.4 did: the same staging folder, the policy without a splitter, and a history entry in its format.
        var repository = await repositories.OpenAsync(target, RepositoryRole.Vm, default);
        var staging = Path.Combine(Path.GetDirectoryName(disk)!, VmBackupService.StagingFolderName, VmId.ToString("N"));
        var links = new WindowsBackupStaging();
        links.Prepare(staging);
        links.Link(disk, Path.Combine(staging, "disk-0.vhdx"));
        await kopia.SetPolicyAsync(repository.Repository, repository.Passphrase, staging, new RetentionPolicy(7, 4, 6), default);
        var old = await kopia.CreateSnapshotAsync(repository.Repository, repository.Passphrase, staging, "hyperharbor 0.2.4", default);
        links.Remove(staging);
        await File.WriteAllTextAsync(Path.Combine(dataDirectory, BackupHistoryStore.FileName), $$"""
            [ { "runId": "{{Guid.NewGuid()}}", "item": "vm:{{VmId:D}}", "vmId": "{{VmId:D}}", "vmName": "Web", "targetId": "t1",
                "trigger": "schedule", "startedAt": "2026-10-27T02:00:00+00:00", "finishedAt": "2026-10-27T02:10:00+00:00",
                "outcome": "succeeded", "reason": null, "snapshotId": "{{old.Id}}", "totalBytes": {{DiskBytes}}, "warnings": [] } ]
            """);

        // 2. The VM was used since (kopia reuses an unchanged file without reading it), then this version backs it up.
        await WriteSectorsAsync(disk, random, 20);
        var inventory = new FakeVmInventory();
        inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Web", VmState.Off));
        var definitions = new FakeDefinitions
        {
            Current = () => new VmDefinition(
                VmId, "Web", 2, null, new ComputeState(2, 2048, 2048, false, false, false, 1), true, null, false, [],
                [new VmDiskDefinition(disk, "scsi", 0, 0)], null, 128, 512),
        };
        var history = new BackupHistoryStore(dataDirectory);
        var service = new VmBackupService(
            inventory, definitions, new FakeCheckpoints([]), new FakeHyperVStorage(), links, repositories, kopia, history,
            new PerformanceStore(dataDirectory), _ => false, TimeProvider.System, NullLogger<VmBackupService>.Instance, "e2e");
        var request = new VmBackupRequest(VmId, target, new BackupPolicy { VmId = VmId.ToString("D"), TargetId = "t1", Retention = new RetentionPolicy(7, 4, 6) }, BackupTrigger.Manual);

        var first = await service.RunAsync(request, (_, _) => { }, default);
        output.WriteLine($"First: {first.Outcome} {first.Reason}, uploaded {Mib(first.UploadedBytes)} MiB; {string.Join(" | ", first.Warnings)}");

        Assert.Equal(BackupOutcome.Succeeded, first.Outcome);
        Assert.True(first.UploadedBytes >= DiskBytes * 0.95, $"uploaded {first.UploadedBytes}");
        Assert.Contains(first.Warnings, w => w.Contains("new layout", StringComparison.Ordinal));
        Assert.Equal(KopiaCli.DiskSplitter, await kopia.ReadSplitterAsync(repository.Repository, repository.Passphrase, staging, default));
        Assert.Equal(2, history.List().Count);
        Assert.Null(history.List().Single(r => r.SnapshotId == old.Id).UploadedBytes);

        // 3. A few scattered writes later, the next backup uploads about one piece per write and warns about nothing.
        await WriteSectorsAsync(disk, random, 20);

        var second = await service.RunAsync(request, (_, _) => { }, default);
        output.WriteLine($"Second: {second.Outcome}, uploaded {Mib(second.UploadedBytes)} MiB; {string.Join(" | ", second.Warnings)}");

        Assert.Equal(BackupOutcome.Succeeded, second.Outcome);
        Assert.InRange(second.UploadedBytes!.Value, 1, 30L << 20);
        Assert.Empty(second.Warnings);
    }

    /// <summary>Overwrites scattered 4 KiB sectors in place, as a guest's small writes do.</summary>
    private static async Task WriteSectorsAsync(string disk, Random random, int count)
    {
        await using var stream = new FileStream(disk, FileMode.Open, FileAccess.Write);
        var sector = new byte[4096];
        for (var i = 0; i < count; i++)
        {
            random.NextBytes(sector);
            stream.Position = (long)random.Next(DiskBytes / sector.Length) * sector.Length;
            await stream.WriteAsync(sector);
        }
    }

    private static string Mib(long? bytes) => ((bytes ?? -1) / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture);
}
