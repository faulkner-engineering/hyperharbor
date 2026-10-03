using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>What a new VM is made of.</summary>
/// <param name="ConfigurationFolder">Null: Hyper-V's default folder.</param>
/// <param name="SwitchId">Null: no network adapter.</param>
public sealed record VmBlueprint(
    string Name,
    string? ConfigurationFolder,
    string DiskPath,
    string IsoPath,
    int ProcessorCount,
    long StartupMemoryMb,
    long MaximumMemoryMb,
    bool DynamicMemory,
    string? SwitchId);

/// <summary>The Hyper-V steps of creating a VM. Each step either completes or throws.</summary>
public interface IHyperVBuilder
{
    /// <summary>Creates a new dynamic VHDX. The file must not exist.</summary>
    Task CreateDiskAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken);

    /// <summary>Defines a Generation 2 VM with Secure Boot (Microsoft Windows template) and the given notes.</summary>
    /// <returns>The new VM's ID.</returns>
    Task<Guid> DefineAsync(VmBlueprint blueprint, string notes, CancellationToken cancellationToken);

    /// <summary>Sets processors and memory, adds the disk, the ISO (first in boot order), and the network adapter.</summary>
    Task ConfigureAsync(Guid vmId, VmBlueprint blueprint, CancellationToken cancellationToken);

    /// <summary>Adds a virtual TPM protected by the host's local (untrusted) guardian.</summary>
    Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken);

    Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVBuilder"/> through root\virtualization\v2 and the HGS client classes.</summary>
public sealed class CimHyperVBuilder : IHyperVBuilder
{
    /// <summary>Secure Boot template "MicrosoftWindows".</summary>
    public const string MicrosoftWindowsTemplateId = "1734c6e8-3154-4dda-ba5f-a874cc483422";

    private const string HgsNamespace = @"root\Microsoft\Windows\Hgs";
    private const string LocalGuardianName = "UntrustedGuardian";

    // Msvm_VirtualHardDiskSettingData.Type and Format.
    private const ushort DynamicDisk = 3;
    private const ushort VhdxFormat = 3;

    public Task CreateDiskAsync(string path, long sizeBytes, Action<int> progress, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_ImageManagementService")
                ?? throw new HyperVUnavailableException("The Hyper-V image management service was not found.");
            var settings = CimXml.Write("Msvm_VirtualHardDiskSettingData",
            [
                new CimXml.Property("Type", CimType.UInt16, DynamicDisk),
                new CimXml.Property("Format", CimType.UInt16, VhdxFormat),
                new CimXml.Property("Path", CimType.String, path),
                new CimXml.Property("MaxInternalSize", CimType.UInt64, (ulong)sizeBytes),
                new CimXml.Property("BlockSize", CimType.UInt32, 0u),
                new CimXml.Property("LogicalSectorSize", CimType.UInt32, 0u),
                new CimXml.Property("PhysicalSectorSize", CimType.UInt32, 0u),
            ]);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("VirtualDiskSettingData", settings, CimType.String, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "CreateVirtualHardDisk", parameters, "CreateVirtualHardDisk", cancellationToken, progress);
        }, cancellationToken);

    public Task<Guid> DefineAsync(VmBlueprint blueprint, string notes, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.ManagementService(session);
            var properties = new List<CimXml.Property>
            {
                new("ElementName", CimType.String, blueprint.Name),
                new("VirtualSystemSubType", CimType.String, "Microsoft:Hyper-V:SubType:2"),
                new("SecureBootEnabled", CimType.Boolean, true),
                new("SecureBootTemplateId", CimType.String, MicrosoftWindowsTemplateId),
                new("Notes", CimType.StringArray, new[] { notes }),
            };
            if (blueprint.ConfigurationFolder is { } folder)
            {
                properties.Add(new("ConfigurationDataRoot", CimType.String, folder));
                properties.Add(new("SnapshotDataRoot", CimType.String, folder));
                properties.Add(new("SwapFileDataRoot", CimType.String, folder));
            }

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SystemSettings", CimXml.Write("Msvm_VirtualSystemSettingData", properties), CimType.String, CimFlags.In),
            };
            var result = await HyperVCim.InvokeAsync(session, service, "DefineSystem", parameters, "DefineSystem", cancellationToken);
            if (result.OutParameters["ResultingSystem"]?.Value is CimInstance system
                && Guid.TryParse(system.CimInstanceProperties["Name"]?.Value as string, out var id))
            {
                return id;
            }

            // Fall back to the name, which the caller has checked is unique.
            using var byName = HyperVCim.QuerySingle(session,
                $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{HyperVCim.Escape(blueprint.Name)}'")
                ?? throw new HyperVOperationException("DefineSystem (the new VM was not found)", uint.MaxValue);
            return Guid.Parse((string)byName.CimInstanceProperties["Name"].Value);
        }, cancellationToken);

    public Task ConfigureAsync(Guid vmId, VmBlueprint blueprint, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);

            using (var processor = CimVmSettings.AssociatedSingle(session, settings, "Msvm_ProcessorSettingData"))
            {
                await CimVmSettings.ModifyResourceAsync(session, processor, cancellationToken,
                    new CimXml.Property("VirtualQuantity", CimType.UInt64, (ulong)blueprint.ProcessorCount));
            }

            using (var memory = CimVmSettings.AssociatedSingle(session, settings, "Msvm_MemorySettingData"))
            {
                await CimVmSettings.ModifyResourceAsync(session, memory, cancellationToken, MemoryChanges(blueprint.StartupMemoryMb, blueprint.MaximumMemoryMb, blueprint.DynamicMemory));
            }

            using var controllerTemplate = CimVmSettings.Default(session, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic SCSI Controller");
            var controller = await CimVmSettings.AddResourceAsync(session, settings, controllerTemplate, cancellationToken);

            // DVD first, at SCSI location 0, so it is also first in the boot order.
            using var dvdTemplate = CimVmSettings.Default(session, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic DVD Drive");
            var dvd = await CimVmSettings.AddResourceAsync(session, settings, dvdTemplate, cancellationToken,
                new("Parent", CimType.String, controller),
                new("AddressOnParent", CimType.String, "0"));
            using var isoTemplate = CimVmSettings.Default(session, "Msvm_StorageAllocationSettingData", "Microsoft:Hyper-V:Virtual CD/DVD Disk");
            await CimVmSettings.AddResourceAsync(session, settings, isoTemplate, cancellationToken,
                new("Parent", CimType.String, dvd),
                new("HostResource", CimType.StringArray, new[] { blueprint.IsoPath }));

            using var driveTemplate = CimVmSettings.Default(session, "Msvm_ResourceAllocationSettingData", "Microsoft:Hyper-V:Synthetic Disk Drive");
            var drive = await CimVmSettings.AddResourceAsync(session, settings, driveTemplate, cancellationToken,
                new("Parent", CimType.String, controller),
                new("AddressOnParent", CimType.String, "1"));
            using var diskTemplate = CimVmSettings.Default(session, "Msvm_StorageAllocationSettingData", "Microsoft:Hyper-V:Virtual Hard Disk");
            await CimVmSettings.AddResourceAsync(session, settings, diskTemplate, cancellationToken,
                new("Parent", CimType.String, drive),
                new("HostResource", CimType.StringArray, new[] { blueprint.DiskPath }));

            if (blueprint.SwitchId is { } switchId)
            {
                using var adapterTemplate = CimVmSettings.Default(session, "Msvm_SyntheticEthernetPortSettingData");
                var adapter = await CimVmSettings.AddResourceAsync(session, settings, adapterTemplate, cancellationToken,
                    new("ElementName", CimType.String, "Network Adapter"),
                    new("VirtualSystemIdentifiers", CimType.StringArray, new[] { Guid.NewGuid().ToString("B") }));
                using var connectionTemplate = CimVmSettings.Default(session, "Msvm_EthernetPortAllocationSettingData");
                await CimVmSettings.AddResourceAsync(session, settings, connectionTemplate, cancellationToken,
                    new("Parent", CimType.String, adapter),
                    new("HostResource", CimType.StringArray, new[] { CimVmSettings.SwitchPath(switchId) }));
            }

            await PutDvdFirstAsync(session, vmId, dvd, cancellationToken);
            return true;
        }, cancellationToken);

    public Task EnableTpmAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            var keyProtector = CreateKeyProtector(session);
            using var settings = CimVmSettings.Realized(session, vmId);
            using var security = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_SecurityService")
                ?? throw new HyperVUnavailableException("The Hyper-V security service was not found.");

            var setKey = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("KeyProtector", keyProtector, CimType.UInt8Array, CimFlags.In),
                CimMethodParameter.Create("SecuritySettingData", CimXml.Write(security), CimType.String, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "SetKeyProtector", setKey, "SetKeyProtector", cancellationToken);

            using var updated = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
            var enable = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SecuritySettingData", CimXml.Write(updated, new CimXml.Property("TpmEnabled", CimType.Boolean, true)), CimType.String, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "ModifySecuritySettings", enable, "ModifySecuritySettings", cancellationToken);
        }, cancellationToken);

    public Task SetNotesAsync(Guid vmId, string notes, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken,
                new CimXml.Property("Notes", CimType.StringArray, string.IsNullOrEmpty(notes) ? Array.Empty<string>() : new[] { notes }));
        }, cancellationToken);

    /// <summary>Memory settings in the form Msvm_MemorySettingData takes.</summary>
    internal static CimXml.Property[] MemoryChanges(long startupMb, long maximumMb, bool dynamic) =>
    [
        new("DynamicMemoryEnabled", CimType.Boolean, dynamic),
        new("VirtualQuantity", CimType.UInt64, (ulong)startupMb),
        new("Reservation", CimType.UInt64, (ulong)(dynamic ? Math.Min(VmSettingsValidator.DynamicMinimumMb, startupMb) : startupMb)),
        new("Limit", CimType.UInt64, (ulong)(dynamic ? maximumMb : startupMb)),
    ];

    /// <summary>
    /// A drive's boot entry has the drive's InstanceID followed by "\B". Moves that entry to the front of
    /// BootSourceOrder and leaves the rest in place.
    /// </summary>
    private static async Task PutDvdFirstAsync(CimSession session, Guid vmId, string dvdPath, CancellationToken cancellationToken)
    {
        using var settings = CimVmSettings.Realized(session, vmId);
        var order = settings.CimInstanceProperties["BootSourceOrder"]?.Value as string[] ?? [];
        var dvdInstanceId = dvdPath[(dvdPath.IndexOf("InstanceID=\"", StringComparison.Ordinal) + "InstanceID=\"".Length)..].TrimEnd('"');
        var dvdBoot = order.FirstOrDefault(entry => entry.Contains(dvdInstanceId + @"\\B""", StringComparison.OrdinalIgnoreCase));
        if (dvdBoot is null || order[0] == dvdBoot)
        {
            return;
        }

        var reordered = new[] { dvdBoot }.Concat(order.Where(entry => entry != dvdBoot)).ToArray();
        await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken, new CimXml.Property("BootSourceOrder", CimType.StringArray, reordered));
    }

    /// <summary>
    /// The local "UntrustedGuardian" (created with self-signed certificates if missing) owns the key
    /// protector, as when a TPM is added in Hyper-V Manager.
    /// </summary>
    private static byte[] CreateKeyProtector(CimSession session)
    {
        var guardian = session.QueryInstances(HgsNamespace, HyperVCim.QueryDialect, $"SELECT * FROM MSFT_HgsGuardian WHERE Name = '{LocalGuardianName}'").FirstOrDefault();
        if (guardian is null)
        {
            var create = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Name", LocalGuardianName, CimType.String, CimFlags.In),
                CimMethodParameter.Create("GenerateCertificates", true, CimType.Boolean, CimFlags.In),
            };
            using var created = session.InvokeMethod(HgsNamespace, "MSFT_HgsGuardian", "NewByGenerateCertificates", create);
            guardian = created.OutParameters["cmdletOutput"]?.Value as CimInstance
                ?? throw new HyperVOperationException("creating the local key protector guardian", HyperVCim.ReturnCode(created));
        }

        using (guardian)
        {
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Owner", guardian, CimType.Instance, CimFlags.In),
                CimMethodParameter.Create("AllowUntrustedRoot", true, CimType.Boolean, CimFlags.In),
            };
            using var result = session.InvokeMethod(HgsNamespace, "MSFT_HgsKeyProtector", "NewByGuardians", parameters);
            var protector = result.OutParameters["cmdletOutput"]?.Value as CimInstance;
            return protector?.CimInstanceProperties["RawData"]?.Value as byte[]
                ?? throw new HyperVOperationException("creating the TPM key protector", HyperVCim.ReturnCode(result));
        }
    }
}
