using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.HyperV;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>
/// Read-only checks of the storage queries against the local Hyper-V installation. Nothing is deleted:
/// only ReadAsync, GetParentPathAsync, and the deletion preview run.
/// </summary>
public sealed class CimHyperVStorageLiveTests(ITestOutputHelper output)
{
    [HyperVFact]
    public async Task ReadAsync_FindsEveryVmsDisks_AndTheirParents()
    {
        var storage = new CimHyperVStorage();

        var snapshot = await storage.ReadAsync(CancellationToken.None);

        foreach (var disk in snapshot.Disks)
        {
            var parent = await storage.GetParentPathAsync(disk.Path, CancellationToken.None);
            output.WriteLine($"{disk.VmName} checkpoint={disk.InCheckpoint} {disk.Path} -> {parent ?? "(no parent)"}");
            Assert.True(DiskPaths.IsDisk(disk.Path), disk.Path);
            if (DiskPaths.IsCheckpointDisk(disk.Path))
            {
                Assert.NotNull(parent);
            }
        }

        foreach (var (vmId, count) in snapshot.CheckpointCounts)
        {
            output.WriteLine($"{vmId}: {count} checkpoint(s)");
        }
    }

    [HyperVFact]
    public async Task Host_ReportsDefaultFoldersAndSwitches()
    {
        var host = new CimHyperVHost();

        var defaults = await host.GetDefaultsAsync(CancellationToken.None);
        var switches = await host.ListSwitchesAsync(CancellationToken.None);

        output.WriteLine($"Config: {defaults.ConfigurationFolder}; disks: {defaults.VirtualHardDiskFolder}");
        switches.ToList().ForEach(item => output.WriteLine($"Switch {item.Name} ({item.Id}) default={item.IsDefault}"));
        Assert.True(Path.IsPathRooted(defaults.VirtualHardDiskFolder));
        Assert.All(switches, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.True(new WindowsHostCapacityReader().Read().TotalMemoryMb > 0);
    }

    [HyperVFact]
    public async Task Compute_ReadsEveryVmsSettings()
    {
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        var compute = new CimHyperVCompute();

        foreach (var vm in await inventory.ListAsync(CancellationToken.None))
        {
            var state = await compute.ReadAsync(vm.Id, CancellationToken.None);

            output.WriteLine($"{vm.Name}: {state}");
            Assert.True(state.ProcessorCount >= 1);
            Assert.True(state.StartupMemoryMb >= 32);
            Assert.True(state.MaximumMemoryMb >= state.StartupMemoryMb || !state.DynamicMemory);
        }
    }

    [HyperVFact]
    public async Task Preview_OfEveryVm_ResolvesBaseDisks()
    {
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        using var jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var deletion = new VmDeletionService(inventory, new CimHyperVStorage(), new WindowsDiskFiles(), jobs, NullLogger<VmDeletionService>.Instance);

        foreach (var vm in await inventory.ListAsync(CancellationToken.None))
        {
            var preview = await deletion.PreviewAsync(vm.Id, CancellationToken.None);

            output.WriteLine($"{preview.VmName} ({preview.State}), {preview.CheckpointCount} checkpoint(s)");
            preview.Disks.ToList().ForEach(disk => output.WriteLine($"  delete: {disk}"));
            preview.Blockers.ToList().ForEach(blocker => output.WriteLine($"  blocker {blocker.Code}/{blocker.Scope}: {blocker.Message}"));
            Assert.All(preview.Disks, disk => Assert.False(DiskPaths.IsCheckpointDisk(disk), $"{disk} is a checkpoint disk; it should have been followed to its base."));
        }
    }
}
