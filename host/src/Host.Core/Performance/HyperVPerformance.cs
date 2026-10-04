using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>The GPU partition a VM gets, in the GPU's own units (the same value for minimum, maximum, and optimal).</summary>
/// <param name="InstancePath">The partitionable GPU; null lets Hyper-V choose.</param>
public sealed record GpuPartitionPlan(string? InstancePath, ulong Vram, ulong Encode, ulong Decode, ulong Compute);

/// <summary>Everything Performance mode sets besides processors and memory.</summary>
public sealed record PerformancePlan(GpuPartitionPlan Gpu, int LowMmioGapMb, int HighMmioGapMb);

/// <summary>The Hyper-V side of Performance mode. Every method expects the VM to be off.</summary>
public interface IHyperVPerformance
{
    Task<bool> HasGpuPartitionAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the VM-level settings (guest-controlled cache types, MMIO gaps, Turn Off stop action,
    /// checkpoints disabled) and adds the GPU partition adapter, or updates the one it has.
    /// </summary>
    Task ApplyAsync(Guid vmId, PerformancePlan plan, CancellationToken cancellationToken);

    /// <summary>Removes the GPU partition adapter and restores Hyper-V's default MMIO, cache, and stop settings.</summary>
    Task RemoveAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>Moves the VM's configuration and virtual hard disks into <paramref name="folder"/>.</summary>
    Task MoveStorageAsync(Guid vmId, string folder, Action<int> progress, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IHyperVPerformance"/> through root\virtualization\v2: ModifySystemSettings on
/// Msvm_VirtualSystemSettingData, Msvm_GpuPartitionSettingData cloned from its Default instance, and
/// Msvm_VirtualSystemMigrationService for storage moves (MigrationType 32769, Storage).
/// </summary>
public sealed class CimHyperVPerformance : IHyperVPerformance
{
    public const string GpuPartitionClass = "Msvm_GpuPartitionSettingData";
    public const string GpuPartitionSubType = "Microsoft:Hyper-V:GPU Partition";

    // Msvm_VirtualSystemSettingData.AutomaticShutdownAction and UserSnapshotType.
    internal const ushort ShutdownActionTurnOff = 2;
    internal const ushort ShutdownActionSave = 3;
    internal const ushort SnapshotsDisabled = 2;

    // Hyper-V's defaults, restored when Performance mode is turned off.
    internal const ulong DefaultLowMmioGapMb = 128;
    internal const ulong DefaultHighMmioGapMb = 512;

    private const ushort StorageMigration = 32769;

    public Task<bool> HasGpuPartitionAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            var partitions = CimVmSettings.Associated(session, settings, GpuPartitionClass);
            var has = partitions.Count > 0;
            Dispose(partitions);
            return Task.FromResult(has);
        }, cancellationToken);

    public Task ApplyAsync(Guid vmId, PerformancePlan plan, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken,
                new CimXml.Property("GuestControlledCacheTypes", CimType.Boolean, true),
                new CimXml.Property("LowMmioGapSize", CimType.UInt64, (ulong)plan.LowMmioGapMb),
                new CimXml.Property("HighMmioGapSize", CimType.UInt64, (ulong)plan.HighMmioGapMb),
                new CimXml.Property("AutomaticShutdownAction", CimType.UInt16, ShutdownActionTurnOff),
                new CimXml.Property("UserSnapshotType", CimType.UInt16, SnapshotsDisabled),
                new CimXml.Property("AutomaticSnapshotsEnabled", CimType.Boolean, false)).ConfigureAwait(false);

            var changes = PartitionProperties(plan.Gpu);
            var existing = CimVmSettings.Associated(session, settings, GpuPartitionClass);
            try
            {
                if (existing.FirstOrDefault() is { } partition)
                {
                    await CimVmSettings.ModifyResourceAsync(session, partition, cancellationToken, changes).ConfigureAwait(false);
                }
                else
                {
                    using var template = CimVmSettings.Default(session, GpuPartitionClass, GpuPartitionSubType);
                    await CimVmSettings.AddResourceAsync(session, settings, template, cancellationToken, changes).ConfigureAwait(false);
                }
            }
            finally
            {
                Dispose(existing);
            }
        }, cancellationToken);

    public Task RemoveAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            var partitions = CimVmSettings.Associated(session, settings, GpuPartitionClass);
            try
            {
                if (partitions.Count > 0)
                {
                    await CimVmSettings.RemoveResourcesAsync(session, partitions, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                Dispose(partitions);
            }

            using var current = CimVmSettings.Realized(session, vmId);
            await CimVmSettings.ModifySystemAsync(session, current, cancellationToken,
                new CimXml.Property("GuestControlledCacheTypes", CimType.Boolean, false),
                new CimXml.Property("LowMmioGapSize", CimType.UInt64, DefaultLowMmioGapMb),
                new CimXml.Property("HighMmioGapSize", CimType.UInt64, DefaultHighMmioGapMb),
                new CimXml.Property("AutomaticShutdownAction", CimType.UInt16, ShutdownActionSave)).ConfigureAwait(false);
        }, cancellationToken);

    public Task MoveStorageAsync(Guid vmId, string folder, Action<int> progress, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
                ?? throw new VmNotFoundException(vmId);
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemMigrationService")
                ?? throw new HyperVUnavailableException("The Hyper-V migration service was not found.");
            using var settings = CimVmSettings.Realized(session, vmId);

            var disks = CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData")
                .Where(item => (item.CimInstanceProperties["ResourceSubType"]?.Value as string) == "Microsoft:Hyper-V:Virtual Hard Disk")
                .ToList();
            try
            {
                var newDisks = disks.Select(disk =>
                {
                    var path = ((string[])disk.CimInstanceProperties["HostResource"].Value)[0];
                    var target = Path.Combine(folder, "Virtual Hard Disks", Path.GetFileName(path));
                    return CimXml.Write(disk, new CimXml.Property("HostResource", CimType.StringArray, new[] { target }));
                }).ToArray();

                using var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("ComputerSystem", system, CimType.Reference, CimFlags.In),
                    CimMethodParameter.Create("DestinationHost", Environment.MachineName, CimType.String, CimFlags.In),
                    CimMethodParameter.Create("MigrationSettingData", CimXml.Write("Msvm_VirtualSystemMigrationSettingData",
                        [new CimXml.Property("MigrationType", CimType.UInt16, StorageMigration)]), CimType.String, CimFlags.In),
                    CimMethodParameter.Create("NewSystemSettingData", CimXml.Write(settings,
                        new CimXml.Property("ConfigurationDataRoot", CimType.String, folder),
                        new CimXml.Property("SnapshotDataRoot", CimType.String, folder),
                        new CimXml.Property("SwapFileDataRoot", CimType.String, folder)), CimType.String, CimFlags.In),
                    CimMethodParameter.Create("NewResourceSettingData", newDisks, CimType.StringArray, CimFlags.In),
                };
                using var result = await HyperVCim.InvokeAsync(session, service, "MigrateVirtualSystemToHost", parameters, "moving the VM's storage", cancellationToken, progress).ConfigureAwait(false);
            }
            finally
            {
                Dispose(disks);
            }
        }, cancellationToken);

    /// <summary>The partition's minimum, maximum, and optimal values, and the GPU it uses when one was chosen.</summary>
    internal static CimXml.Property[] PartitionProperties(GpuPartitionPlan gpu)
    {
        var properties = new List<CimXml.Property>();
        foreach (var (resource, value) in new[] { ("VRAM", gpu.Vram), ("Encode", gpu.Encode), ("Decode", gpu.Decode), ("Compute", gpu.Compute) })
        {
            properties.Add(new($"MinPartition{resource}", CimType.UInt64, value));
            properties.Add(new($"MaxPartition{resource}", CimType.UInt64, value));
            properties.Add(new($"OptimalPartition{resource}", CimType.UInt64, value));
        }

        if (gpu.InstancePath is { } path)
        {
            properties.Add(new("HostResource", CimType.StringArray, new[] { path }));
        }

        return [.. properties];
    }

    private static void Dispose(IEnumerable<CimInstance> instances)
    {
        foreach (var instance in instances)
        {
            instance.Dispose();
        }
    }
}
