using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Management.Infrastructure;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Performance;

/// <summary>
/// Creates a throwaway VM named HyperHarbor-Test, applies Performance mode with this host's partitionable
/// GPU (and a storage move), exports its disk, reads every setting back from Hyper-V, turns Performance
/// mode off, and deletes the VM. The VM is never started. Changes the host, so it runs only when
/// HH_PERFORMANCE_LIVE is set: HH_PERFORMANCE_LIVE=1 dotnet test --filter PerformanceLiveTests
/// </summary>
public sealed class PerformanceLiveTests(ITestOutputHelper output) : IDisposable
{
    private const string VmName = "HyperHarbor-Test";
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [EnvironmentFact("HH_PERFORMANCE_LIVE")]
    public async Task ApplyExportRemove_OnAThrowawayVm()
    {
        var isoFolder = Path.Combine(_root, "isos");
        Directory.CreateDirectory(isoFolder);
        await File.WriteAllBytesAsync(Path.Combine(isoFolder, "placeholder.iso"), new byte[2048]);

        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        Assert.DoesNotContain(await inventory.ListAsync(CancellationToken.None), vm => vm.Name == VmName);

        var gpus = await new CimHostGpuReader().ReadAsync(CancellationToken.None);
        var gpu = Assert.Single(gpus, item => item.Partitionable);
        output.WriteLine($"GPU: {gpu.Name} {gpu.Vendor} {gpu.DriverVersion} {gpu.InstancePath} {gpu.Capacity}");

        var storage = new CimHyperVStorage();
        var files = new WindowsDiskFiles();
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms"), IsoFolder = isoFolder, HostMemoryReserveMb = 0 };
        var locks = new VmOperationLocks();
        using var jobs = new VmJobStore(locks, TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var creation = new VmCreationService(
            inventory, new CimHyperVBuilder(), storage, new CimHyperVHost(), new WindowsHostCapacityReader(), files,
            new IsoLibrary(isoFolder), jobs, options, NullLogger<VmCreationService>.Instance);
        var created = await creation.StartAsync(
            UserId,
            new CreateVmRequest(VmName, "placeholder.iso", 1, 2, 1024, 2048, DynamicMemory: true, AcknowledgeWarnings: true),
            null,
            CancellationToken.None);
        await jobs.WhenFinished(created.Id);
        var createJob = jobs.Get(created.Id, UserId)!;
        output.WriteLine($"Create: {createJob.State} {createJob.ErrorTitle} {createJob.ErrorDetail}");
        Assert.Equal(VmJobState.Succeeded, createJob.State);
        var vmId = createJob.VmId!.Value;

        var dataDirectory = Path.Combine(_root, "data");
        Directory.CreateDirectory(dataDirectory);
        var hyperV = new CimHyperVPerformance();
        var performance = new VmPerformanceService(
            inventory, hyperV, new CimHyperVCompute(), new CimHostGpuReader(), new WindowsHostCapacityReader(),
            new PerformanceStore(dataDirectory), new Core.Provisioning.VmCredentialStore(dataDirectory), new CimGpuDriverSource(),
            new PowerShellDirectPerformanceSetup(), locks, jobs, options, TimeProvider.System, NullLogger<VmPerformanceService>.Instance);
        var moved = Path.Combine(_root, "moved");

        try
        {
            var settings = new PerformanceSettings(
                2, 1024, new GpuPartitionShare(gpu.InstancePath, 40, 50, 60, 70), new MmioSettings(1024, 32768), moved,
                new PerformanceRdpSettings(), AcknowledgeWarnings: true);
            var applied = await performance.ApplyAsync(vmId, UserId, settings, null, CancellationToken.None);
            await jobs.WhenFinished(applied.Id);
            var applyJob = jobs.Get(applied.Id, UserId)!;
            output.WriteLine($"Apply: {applyJob.State} {applyJob.ErrorTitle} {applyJob.ErrorDetail}");
            Assert.Equal(VmJobState.Succeeded, applyJob.State);

            var state = await performance.GetAsync(vmId, CancellationToken.None);
            Assert.True(state.Enabled);
            Assert.True(state.GpuAttached);
            CheckApplied(vmId, gpu.Capacity!, moved);

            var export = new VmDiskExportService(
                inventory, storage, files, new FileDiskCopier(), new BackupLocation(new HostSettingsStore(dataDirectory)), jobs,
                TimeProvider.System, NullLogger<VmDiskExportService>.Instance);
            var backups = Path.Combine(_root, "backups");
            var exporting = await export.StartAsync(vmId, UserId, backups, null, CancellationToken.None);
            await jobs.WhenFinished(exporting.Id);
            var exportJob = jobs.Get(exporting.Id, UserId)!;
            output.WriteLine($"Export: {exportJob.State} {exportJob.Step} {exportJob.ErrorDetail}");
            Assert.Equal(VmJobState.Succeeded, exportJob.State);
            var copy = Assert.Single(Directory.GetFiles(Assert.Single(Directory.GetDirectories(backups))));
            Assert.Equal(VmName + ".vhdx", Path.GetFileName(copy));

            await performance.RemoveAsync(vmId, CancellationToken.None);
            Assert.False((await performance.GetAsync(vmId, CancellationToken.None)).GpuAttached);
            CheckRemoved(vmId);
        }
        finally
        {
            var deletion = new VmDeletionService(inventory, storage, files, jobs, NullLogger<VmDeletionService>.Instance);
            var deleting = await deletion.StartAsync(vmId, UserId, new VmDeleteRequest(true, true, VmName), null, CancellationToken.None);
            await jobs.WhenFinished(deleting.Id);
            var deleted = jobs.Get(deleting.Id, UserId)!;
            output.WriteLine($"Delete: {deleted.State} {deleted.ErrorDetail}");
            Assert.Equal(VmJobState.Succeeded, deleted.State);
        }

        Assert.DoesNotContain(await inventory.ListAsync(CancellationToken.None), vm => vm.Id == vmId);
    }

    private void CheckApplied(Guid vmId, PartitionCapacity capacity, string moved)
    {
        using var session = CimSession.Create(null);
        using var settings = SystemSettings(session, vmId);
        Print(settings, "GuestControlledCacheTypes", "LowMmioGapSize", "HighMmioGapSize", "AutomaticShutdownAction", "UserSnapshotType", "AutomaticSnapshotsEnabled", "ConfigurationDataRoot");
        Assert.Equal(true, settings.CimInstanceProperties["GuestControlledCacheTypes"].Value);
        Assert.Equal(1024UL, Convert.ToUInt64(settings.CimInstanceProperties["LowMmioGapSize"].Value));
        Assert.Equal(32768UL, Convert.ToUInt64(settings.CimInstanceProperties["HighMmioGapSize"].Value));
        Assert.Equal(2, Convert.ToInt32(settings.CimInstanceProperties["AutomaticShutdownAction"].Value));
        Assert.Equal(2, Convert.ToInt32(settings.CimInstanceProperties["UserSnapshotType"].Value));
        Assert.Equal(false, settings.CimInstanceProperties["AutomaticSnapshotsEnabled"].Value);
        Assert.StartsWith(moved, (string)settings.CimInstanceProperties["ConfigurationDataRoot"].Value, StringComparison.OrdinalIgnoreCase);

        var memory = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_MemorySettingData", null, null).Single();
        Assert.Equal(false, memory.CimInstanceProperties["DynamicMemoryEnabled"].Value);
        Assert.Equal(1024UL, memory.CimInstanceProperties["VirtualQuantity"].Value);
        var processor = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_ProcessorSettingData", null, null).Single();
        Assert.Equal(2UL, processor.CimInstanceProperties["VirtualQuantity"].Value);

        var partition = Assert.Single(session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, CimHyperVPerformance.GpuPartitionClass, null, null));
        Print(partition, "HostResource", "MinPartitionVRAM", "MaxPartitionVRAM", "OptimalPartitionVRAM", "MaxPartitionEncode", "MaxPartitionDecode", "MaxPartitionCompute");
        Assert.Contains("Msvm_PartitionableGpu", Assert.Single((string[])partition.CimInstanceProperties["HostResource"].Value), StringComparison.Ordinal);
        Assert.Equal(GpuIdentity.Share(capacity.Vram, 40),Convert.ToUInt64(partition.CimInstanceProperties["MaxPartitionVRAM"].Value));
        Assert.Equal(GpuIdentity.Share(capacity.Encode, 50), Convert.ToUInt64(partition.CimInstanceProperties["MaxPartitionEncode"].Value));
        Assert.Equal(GpuIdentity.Share(capacity.Decode, 60), Convert.ToUInt64(partition.CimInstanceProperties["MaxPartitionDecode"].Value));
        Assert.Equal(GpuIdentity.Share(capacity.Compute, 70), Convert.ToUInt64(partition.CimInstanceProperties["MaxPartitionCompute"].Value));

        var disk = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_StorageAllocationSettingData", null, null)
            .Select(allocation => (allocation.CimInstanceProperties["HostResource"].Value as string[])?.FirstOrDefault())
            .Single(path => path?.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) == true);
        output.WriteLine($"Disk: {disk}");
        Assert.StartsWith(moved, disk, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(disk), $"{disk} is missing after the move.");
    }

    private void CheckRemoved(Guid vmId)
    {
        using var session = CimSession.Create(null);
        using var settings = SystemSettings(session, vmId);
        Print(settings, "LowMmioGapSize", "HighMmioGapSize", "AutomaticShutdownAction");
        Assert.Empty(session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, CimHyperVPerformance.GpuPartitionClass, null, null));
        Assert.Equal(128UL, Convert.ToUInt64(settings.CimInstanceProperties["LowMmioGapSize"].Value));
        Assert.Equal(512UL, Convert.ToUInt64(settings.CimInstanceProperties["HighMmioGapSize"].Value));
        Assert.Equal(3, Convert.ToInt32(settings.CimInstanceProperties["AutomaticShutdownAction"].Value));
    }

    private static CimInstance SystemSettings(CimSession session, Guid vmId) =>
        session.QueryInstances(HyperVCim.Namespace, "WQL",
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'").Single();

    private void Print(CimInstance instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = instance.CimInstanceProperties[name]?.Value;
            output.WriteLine($"{instance.CimClass.CimSystemProperties.ClassName}.{name} = {(value is Array array ? string.Join("|", array.Cast<object>()) : value)}");
        }
    }
}
