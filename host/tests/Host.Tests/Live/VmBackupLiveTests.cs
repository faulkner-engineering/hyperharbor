using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Tests.Backup;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Backs up a throwaway VM (HyperHarbor-BackupTest, 1 GB disk, no operating system) through the real Hyper-V and Kopia code:
/// off (direct copy), then running (checkpoint, frozen parent, checkpoint removed), and restores the files from the repository.
/// Creates and deletes only that VM. HH_BACKUP_VM_LIVE=1 and HH_KOPIA_EXE=&lt;path to kopia.exe&gt;.
/// </summary>
public sealed class VmBackupLiveTests(ITestOutputHelper output) : IDisposable
{
    private const string VmName = "HyperHarbor-BackupTest";
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [EnvironmentFact("HH_BACKUP_VM_LIVE", "HH_KOPIA_EXE")]
    public async Task Live_BackupOfAnOffAndARunningVm_RestoresAndLeavesNoCheckpoint()
    {
        var isoFolder = Directory.CreateDirectory(Path.Combine(_root, "isos")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(isoFolder, "placeholder.iso"), HyperHarbor.Host.Core.Unattend.SeedIso.Build("PLACEHOLDER", new Dictionary<string, string> { ["readme.txt"] = "x" }));
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        Assert.DoesNotContain(await inventory.ListAsync(default), vm => vm.Name == VmName);

        var storage = new CimHyperVStorage();
        var files = new WindowsDiskFiles();
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms"), IsoFolder = isoFolder, HostMemoryReserveMb = 0 };
        using var jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var creation = new VmCreationService(
            inventory, new CimHyperVBuilder(), storage, new CimHyperVHost(), new WindowsHostCapacityReader(), files,
            new IsoLibrary(isoFolder), jobs, options, NullLogger<VmCreationService>.Instance);
        var created = await creation.StartAsync(
            UserId, new CreateVmRequest(VmName, "placeholder.iso", 1, 1, 1024, 1024, DynamicMemory: false, AcknowledgeWarnings: true), null, default);
        await jobs.WhenFinished(created.Id);
        var job = jobs.Get(created.Id, UserId)!;
        Assert.Equal(VmJobState.Succeeded, job.State);
        var vmId = job.VmId!.Value;
        var power = new CimHyperVPowerInvoker();

        try
        {
            var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
            var secrets = new MemorySecrets();
            secrets.Set(RecoveryKeyService.PassphraseReference("repo-live"), "live-passphrase");
            var target = new BackupTarget
            {
                Id = "t1", Name = "Live", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(Path.Combine(_root, "dest")),
                VmRepository = new RepositoryRef("repo-live", RepositoryKeyState.Confirmed),
            };
            var kopia = new KopiaCli(new KopiaProcessRunner(Environment.GetEnvironmentVariable("HH_KOPIA_EXE")!));
            var checkpoints = new CimHyperVCheckpoints();
            var definitions = new CimHyperVDefinitionReader(new CimHyperVCompute());
            var history = new BackupHistoryStore(data);
            var service = new VmBackupService(
                inventory, definitions, checkpoints, storage, new WindowsBackupStaging(), new BackupRepositories(kopia, secrets, data), kopia,
                history, new PerformanceStore(data), _ => false, TimeProvider.System, NullLogger<VmBackupService>.Instance, "live");
            var request = new VmBackupRequest(vmId, target, new BackupPolicy { VmId = vmId.ToString("D"), TargetId = "t1" }, BackupTrigger.Manual);

            // The definition reader against real Hyper-V.
            var definition = await definitions.ReadAsync(vmId, default);
            output.WriteLine($"Definition: {definition}");
            Assert.Equal(VmName, definition.Name);
            Assert.Equal(2, definition.Generation);
            Assert.True(definition.SecureBoot);
            Assert.Equal(1, definition.Compute.ProcessorCount);
            var disk = Assert.Single(definition.Disks);
            Assert.EndsWith(".vhdx", disk.Path, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("scsi", disk.ControllerType);
            Assert.Contains(definition.Adapters, a => a.SwitchId is not null);

            // 1. Off: a direct copy.
            var off = await service.RunAsync(request, (step, percent) => output.WriteLine($"  {percent}% {step}"), default);
            output.WriteLine($"Off backup: {off.Outcome} {off.Reason} snapshot {off.SnapshotId}, {off.TotalBytes} bytes; {string.Join(" | ", off.Warnings)}");
            Assert.Equal(BackupOutcome.Succeeded, off.Outcome);

            // 2. Running: a checkpoint (production if the guest allows, else standard), removed afterwards.
            await power.InvokeAsync(vmId, VmAction.Start, default);
            await WaitForStateAsync(inventory, vmId, VmState.Running);
            var running = await service.RunAsync(request, (step, percent) => output.WriteLine($"  {percent}% {step}"), default);
            output.WriteLine($"Running backup: {running.Outcome} {running.Reason} snapshot {running.SnapshotId}; {string.Join(" | ", running.Warnings)}");
            Assert.Equal(BackupOutcome.Succeeded, running.Outcome);
            Assert.Empty(await checkpoints.ListAsync(vmId, default));
            var after = await definitions.ReadAsync(vmId, default);
            Assert.Equal(disk.Path, Assert.Single(after.Disks).Path, ignoreCase: true);
            Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(disk.Path)!, VmBackupService.StagingFolderName, vmId.ToString("N"))));

            // 3. The repository holds both, and the latest restores to the original size with a readable manifest.
            var repository = KopiaBackendFactory.For(target, RepositoryRole.Vm, "repo-live", data, secrets);
            var snapshots = await kopia.ListSnapshotsAsync(repository, "live-passphrase", default);
            // Daily retention keeps one snapshot per day, so the second backup replaced the first.
            Assert.Equal(running.SnapshotId, Assert.Single(snapshots).Id);
            var restored = Path.Combine(_root, "restored");
            await kopia.RestoreAsync(repository, "live-passphrase", running.SnapshotId!, restored, default);
            var manifest = BackupManifest.Parse(await File.ReadAllBytesAsync(Path.Combine(restored, BackupManifest.FileName)));
            output.WriteLine($"Manifest consistency: {manifest.Consistency.Mode}");
            Assert.Equal(vmId, manifest.Vm.Id);
            Assert.Equal(new FileInfo(disk.Path).Length, new FileInfo(Path.Combine(restored, manifest.Disks[0].File)).Length);
        }
        finally
        {
            try
            {
                await power.InvokeAsync(vmId, VmAction.TurnOff, default);
                await WaitForStateAsync(inventory, vmId, VmState.Off);
            }
            catch (Exception ex) when (ex is VmActionNotAllowedException or TimeoutException)
            {
            }

            var deleting = await new VmDeletionService(inventory, storage, files, jobs, NullLogger<VmDeletionService>.Instance)
                .StartAsync(vmId, UserId, new VmDeleteRequest(true, true, VmName), null, default);
            await jobs.WhenFinished(deleting.Id);
            output.WriteLine($"Delete: {jobs.Get(deleting.Id, UserId)!.State}");
        }

        Assert.DoesNotContain(await inventory.ListAsync(default), vm => vm.Name == VmName);
    }

    private static async Task WaitForStateAsync(IVmInventory inventory, Guid vmId, VmState state)
    {
        for (var i = 0; i < 60; i++)
        {
            if ((await inventory.GetAsync(vmId, default))?.State == state)
            {
                return;
            }

            await Task.Delay(1000);
        }

        throw new TimeoutException($"The VM did not reach {state}.");
    }
}
