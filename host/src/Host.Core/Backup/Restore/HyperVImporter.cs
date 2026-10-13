using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Backup.Restore;

public sealed record ImportDisk(string Path, int Controller, int Lun);

/// <param name="SwitchId">Null: the adapter is created without a connection.</param>
public sealed record ImportAdapter(string? SwitchId);

/// <summary>Hardware of a VM to define from a backup manifest. Names and paths are already resolved.</summary>
public sealed record ImportPlan(
    string Name,
    string ConfigurationFolder,
    string? Notes,
    int ProcessorCount,
    long StartupMemoryMb,
    long MaximumMemoryMb,
    bool DynamicMemory,
    bool SecureBoot,
    string? SecureBootTemplateId,
    IReadOnlyList<ImportDisk> Disks,
    IReadOnlyList<ImportAdapter> Adapters);

/// <summary>Defines a VM from restored disks. Each method either completes or throws.</summary>
public interface IHyperVImporter
{
    /// <summary>Defines a Generation 2 VM with the plan's processors, memory, firmware, disks, and adapters.</summary>
    /// <returns>The new VM's ID.</returns>
    Task<Guid> CreateAsync(ImportPlan plan, CancellationToken cancellationToken);

    /// <summary>Gives the VM a virtual TPM under a freshly created key protector; the original protector is never reused.</summary>
    Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVImporter"/> through root\virtualization\v2, reusing the builder's cloning of Default templates.</summary>
public sealed class CimHyperVImporter(IHyperVBuilder builder) : IHyperVImporter
{
    public async Task<Guid> CreateAsync(ImportPlan plan, CancellationToken cancellationToken)
    {
        if (plan.Disks.Count == 0)
        {
            throw new InvalidOperationException("A VM needs at least one disk.");
        }

        var blueprint = new VmBlueprint(
            plan.Name, plan.ConfigurationFolder, plan.Disks[0].Path, string.Empty, plan.ProcessorCount,
            plan.StartupMemoryMb, plan.MaximumMemoryMb, plan.DynamicMemory, null,
            plan.SecureBootTemplateId ?? CimHyperVBuilder.MicrosoftWindowsTemplateId);
        var vmId = await builder.DefineAsync(blueprint, plan.Notes ?? string.Empty, cancellationToken).ConfigureAwait(false);
        await HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using (var processor = CimVmSettings.AssociatedSingle(session, settings, "Msvm_ProcessorSettingData"))
            {
                await CimVmSettings.ModifyResourceAsync(session, processor, cancellationToken,
                    new CimXml.Property("VirtualQuantity", CimType.UInt64, (ulong)plan.ProcessorCount)).ConfigureAwait(false);
            }

            using (var memory = CimVmSettings.AssociatedSingle(session, settings, "Msvm_MemorySettingData"))
            {
                await CimVmSettings.ModifyResourceAsync(session, memory, cancellationToken,
                    CimHyperVBuilder.MemoryChanges(plan.StartupMemoryMb, plan.MaximumMemoryMb, plan.DynamicMemory)).ConfigureAwait(false);
            }

            using var controllerTemplate = CimVmSettings.Default(session, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic SCSI Controller");
            using var driveTemplate = CimVmSettings.Default(session, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic Disk Drive");
            using var diskTemplate = CimVmSettings.Default(session, "Msvm_StorageAllocationSettingData", "Microsoft:Hyper-V:Virtual Hard Disk");
            var controllers = new Dictionary<int, string>();
            foreach (var disk in plan.Disks)
            {
                if (!controllers.TryGetValue(disk.Controller, out var controller))
                {
                    controller = await CimVmSettings.AddResourceAsync(session, settings, controllerTemplate, cancellationToken).ConfigureAwait(false);
                    controllers[disk.Controller] = controller;
                }

                var drive = await CimVmSettings.AddResourceAsync(session, settings, driveTemplate, cancellationToken,
                    new("Parent", CimType.String, controller),
                    new("AddressOnParent", CimType.String, disk.Lun.ToString(System.Globalization.CultureInfo.InvariantCulture))).ConfigureAwait(false);
                await CimVmSettings.AddResourceAsync(session, settings, diskTemplate, cancellationToken,
                    new("Parent", CimType.String, drive),
                    new("HostResource", CimType.StringArray, new[] { disk.Path })).ConfigureAwait(false);
            }

            foreach (var adapter in plan.Adapters)
            {
                using var adapterTemplate = CimVmSettings.Default(session, "Msvm_SyntheticEthernetPortSettingData");
                var port = await CimVmSettings.AddResourceAsync(session, settings, adapterTemplate, cancellationToken,
                    new("ElementName", CimType.String, "Network Adapter"),
                    new("VirtualSystemIdentifiers", CimType.StringArray, new[] { Guid.NewGuid().ToString("B") })).ConfigureAwait(false);
                if (adapter.SwitchId is { } switchId)
                {
                    using var connectionTemplate = CimVmSettings.Default(session, "Msvm_EthernetPortAllocationSettingData");
                    await CimVmSettings.AddResourceAsync(session, settings, connectionTemplate, cancellationToken,
                        new("Parent", CimType.String, port),
                        new("HostResource", CimType.StringArray, new[] { CimVmSettings.SwitchPath(switchId) })).ConfigureAwait(false);
                }
            }

            if (!plan.SecureBoot)
            {
                await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken,
                    new CimXml.Property("SecureBootEnabled", CimType.Boolean, false)).ConfigureAwait(false);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);
        return vmId;
    }

    public Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken) => builder.EnableTpmAsync(vmId, cancellationToken);
}
