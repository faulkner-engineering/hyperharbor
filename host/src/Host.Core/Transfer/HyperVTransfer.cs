using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>A transfer could not go on; the message says why in words for the user.</summary>
public sealed class TransferException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What ExportSystemDefinition wrote: the definition file and everything beside it (guest state, runtime file).</summary>
/// <param name="Files">Every exported file, as full paths.</param>
public sealed record ExportedDefinition(string DefinitionFile, IReadOnlyList<string> Files);

/// <summary>
/// The Hyper-V side of a transfer. The source exports a VM's definition without its storage; the target imports
/// it as a planned system, changes it for this host, validates it, and realizes it. Verified on one PC by the
/// Phase 24.1 spike (TransferSpikeLiveTests) and TransferCimLiveTests.
/// </summary>
public interface IHyperVTransfer
{
    /// <summary>Exports the definition only (no disks, no runtime state, no checkpoints) into its own subfolder of <paramref name="folder"/>.</summary>
    Task<ExportedDefinition> ExportAsync(Guid vmId, string folder, CancellationToken cancellationToken);

    /// <summary>
    /// Imports an exported definition as a planned system. Hyper-V copies the files into its own folder, so
    /// <paramref name="definitionFile"/> and the files beside it can be deleted once the system is realized.
    /// </summary>
    /// <param name="newIdentity">True for a copy: Hyper-V gives the VM a new ID.</param>
    /// <returns>The planned system's ID, which the realized VM keeps.</returns>
    Task<Guid> ImportPlannedAsync(string definitionFile, bool newIdentity, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the planned system for this host: name, data folders, disks, DVD images ejected, network
    /// connections and MAC addresses, GPU partition removed, and automatic start turned off until the
    /// transfer completes.
    /// </summary>
    /// <returns>The automatic start action the VM had, to put back when the transfer completes.</returns>
    Task<ushort> FixPlannedAsync(Guid plannedId, TransferPlan plan, CancellationToken cancellationToken);

    Task ValidatePlannedAsync(Guid plannedId, CancellationToken cancellationToken);

    Task RealizePlannedAsync(Guid plannedId, CancellationToken cancellationToken);

    /// <summary>Removes a planned system, if it still exists. Its disks are not touched.</summary>
    Task DeletePlannedAsync(Guid plannedId, CancellationToken cancellationToken);

    Task SetAutomaticStartupActionAsync(Guid vmId, ushort action, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVTransfer"/> through root\virtualization\v2.</summary>
public sealed class CimHyperVTransfer : IHyperVTransfer
{
    /// <summary>Msvm_VirtualSystemSettingData.AutomaticStartupAction "None".</summary>
    public const ushort StartupActionNone = 2;

    private const string PlannedType = "Microsoft:Hyper-V:System:Planned";
    private const string DiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";
    private const string ImageSubType = "Microsoft:Hyper-V:Virtual CD/DVD Disk";

    /// <summary>
    /// The folders Hyper-V keeps VM files in. GuestStateDataRoot is left out on purpose: a planned system refuses
    /// it (0x80041008, seen live), and the guest state file follows ConfigurationDataRoot when the system is realized.
    /// </summary>
    private static readonly string[] DataRoots = ["ConfigurationDataRoot", "SnapshotDataRoot", "SwapFileDataRoot", "SuspendDataRoot", "LogDataRoot"];

    public Task<ExportedDefinition> ExportAsync(Guid vmId, string folder, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            Directory.CreateDirectory(folder);
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
                ?? throw new Power.VmNotFoundException(vmId);
            using var template = session.EnumerateAssociatedInstances(HyperVCim.Namespace, system, null, "Msvm_VirtualSystemExportSettingData", null, null).Single();
            var settings = CimXml.Write(template,
                new("CopyVmStorage", CimType.Boolean, false),
                new("CopyVmRuntimeInformation", CimType.Boolean, false),
                new("CopySnapshotConfiguration", CimType.UInt8, (byte)1), // ExportNoSnapshots
                new("CreateVmExportSubdirectory", CimType.Boolean, true));
            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("ComputerSystem", system, CimType.Reference, CimFlags.In),
                CimMethodParameter.Create("ExportDirectory", folder, CimType.String, CimFlags.In),
                CimMethodParameter.Create("ExportSettingData", settings, CimType.String, CimFlags.In),
            };
            using var result = await HyperVCim.InvokeAsync(session, service, "ExportSystemDefinition", parameters, "ExportSystemDefinition", cancellationToken).ConfigureAwait(false);

            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
            var definition = files.Where(file => file.EndsWith(".vmcx", StringComparison.OrdinalIgnoreCase)).ToList();
            if (definition.Count != 1)
            {
                throw new TransferException($"Exporting the VM's definition produced {definition.Count} definition files instead of one.");
            }

            return new ExportedDefinition(definition[0], files);
        }, cancellationToken);

    public Task<Guid> ImportPlannedAsync(string definitionFile, bool newIdentity, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            // The exported layout is <folder>\<name>\Virtual Machines\<id>.vmcx; the import wants a snapshot folder beside it.
            var snapshots = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(definitionFile)!)!, "Snapshots");
            Directory.CreateDirectory(snapshots);
            var before = PlannedIds(session);

            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SystemDefinitionFile", definitionFile, CimType.String, CimFlags.In),
                CimMethodParameter.Create("SnapshotFolder", snapshots, CimType.String, CimFlags.In),
                CimMethodParameter.Create("GenerateNewSystemIdentifier", newIdentity, CimType.Boolean, CimFlags.In),
            };
            using var result = await HyperVCim.InvokeAsync(session, service, "ImportSystemDefinition", parameters, "ImportSystemDefinition", cancellationToken).ConfigureAwait(false);
            if (result.OutParameters["ImportedSystem"]?.Value is CimInstance imported
                && Guid.TryParse(imported.CimInstanceProperties["Name"]?.Value as string, out var id))
            {
                return id;
            }

            // When the import ran as a job, the reference is not filled in: the planned system is the new one.
            var added = PlannedIds(session).Except(before).ToList();
            return added.Count == 1
                ? added[0]
                : throw new TransferException($"Hyper-V's import left {added.Count} new planned virtual machines; expected one.");
        }, cancellationToken);

    public Task<ushort> FixPlannedAsync(Guid plannedId, TransferPlan plan, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = PlannedSettings(session, plannedId);
            var originalStartup = settings.CimInstanceProperties["AutomaticStartupAction"]?.Value is ushort action ? action : StartupActionNone;
            var changes = DataRoots.Where(root => settings.CimInstanceProperties[root] is not null)
                .Select(root => new CimXml.Property(root, CimType.String, plan.VmFolder))
                .Append(new CimXml.Property("ElementName", CimType.String, plan.Name))
                .Append(new CimXml.Property("AutomaticStartupAction", CimType.UInt16, StartupActionNone))
                .ToArray();
            await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken, changes).ConfigureAwait(false);

            await FixStorageAsync(session, settings, plan, cancellationToken).ConfigureAwait(false);
            await FixNetworkAsync(session, settings, plan, cancellationToken).ConfigureAwait(false);

            var gpus = CimVmSettings.Associated(session, settings, "Msvm_GpuPartitionSettingData");
            if (gpus.Count > 0)
            {
                await CimVmSettings.RemoveResourcesAsync(session, gpus, cancellationToken).ConfigureAwait(false);
            }

            gpus.ForEach(gpu => gpu.Dispose());
            return originalStartup;
        }, cancellationToken);

    public Task ValidatePlannedAsync(Guid plannedId, CancellationToken cancellationToken) =>
        InvokeOnPlannedAsync(plannedId, "ValidatePlannedSystem", cancellationToken);

    public Task RealizePlannedAsync(Guid plannedId, CancellationToken cancellationToken) =>
        InvokeOnPlannedAsync(plannedId, "RealizePlannedSystem", cancellationToken);

    public Task DeletePlannedAsync(Guid plannedId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var planned = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_PlannedComputerSystem WHERE Name = '{plannedId:D}'");
            if (planned is null)
            {
                return;
            }

            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection { CimMethodParameter.Create("AffectedSystem", planned, CimType.Reference, CimFlags.In) };
            using var result = await HyperVCim.InvokeAsync(session, service, "DestroySystem", parameters, "DestroySystem", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task SetAutomaticStartupActionAsync(Guid vmId, ushort action, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            await CimVmSettings.ModifySystemAsync(session, settings, cancellationToken,
                new CimXml.Property("AutomaticStartupAction", CimType.UInt16, action)).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>Points each disk at its received file and ejects DVD images, which stay on the source host.</summary>
    private static async Task FixStorageAsync(CimSession session, CimInstance settings, TransferPlan plan, CancellationToken cancellationToken)
    {
        var storage = CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData");
        try
        {
            var placed = new HashSet<string>(DiskPaths.Comparer);
            foreach (var item in storage)
            {
                var subType = item.CimInstanceProperties["ResourceSubType"]?.Value as string;
                var current = item.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts ? hosts[0] : null;
                if (subType == DiskSubType && current is not null)
                {
                    var disk = plan.Disks.FirstOrDefault(candidate => DiskPaths.Comparer.Equals(DiskPaths.Normalize(candidate.SourcePath), DiskPaths.Normalize(current)))
                        ?? throw new TransferException($"The VM's definition names the disk {current}, which was not sent.");
                    await CimVmSettings.ModifyResourceAsync(session, item, cancellationToken,
                        new CimXml.Property("HostResource", CimType.StringArray, new[] { disk.TargetPath })).ConfigureAwait(false);
                    placed.Add(disk.TargetPath);
                }
                else if (subType == ImageSubType)
                {
                    await CimVmSettings.RemoveResourcesAsync(session, [item], cancellationToken).ConfigureAwait(false);
                }
            }

            if (plan.Disks.FirstOrDefault(disk => !placed.Contains(disk.TargetPath)) is { } unused)
            {
                throw new TransferException($"The VM's definition does not use the disk {unused.SourceFileName} that was sent.");
            }
        }
        finally
        {
            storage.ForEach(item => item.Dispose());
        }
    }

    /// <summary>
    /// Adapters are matched to the plan in the order Hyper-V lists them, the same order the source read them in
    /// (a copy's adapters have no address yet, so the address cannot be the key). Each gets the planned switch or
    /// no connection, and a static address (a move) or a new dynamic one (a copy).
    /// </summary>
    private static async Task FixNetworkAsync(CimSession session, CimInstance settings, TransferPlan plan, CancellationToken cancellationToken)
    {
        var ports = CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData");
        var connections = CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData");
        try
        {
            if (ports.Count != plan.Adapters.Count)
            {
                throw new TransferException(
                    $"The VM's definition has {ports.Count} network adapter(s) where {plan.Adapters.Count} were expected. Legacy network adapters cannot be transferred.");
            }

            for (var i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                var adapter = plan.Adapters[i];
                CimXml.Property[] mac = adapter.StaticMacAddress is { } address
                    ? [new("StaticMacAddress", CimType.Boolean, true), new("Address", CimType.String, address)]
                    : [new("StaticMacAddress", CimType.Boolean, false), new("Address", CimType.String, string.Empty)];
                await CimVmSettings.ModifyResourceAsync(session, port, cancellationToken, mac).ConfigureAwait(false);

                var portId = (string)port.CimInstanceProperties["InstanceID"].Value;
                var connection = connections.FirstOrDefault(item =>
                    CimHyperVDefinitionReader.ParentMatches(item.CimInstanceProperties["Parent"]?.Value as string, portId));
                if (adapter.SwitchId is null)
                {
                    if (connection is not null)
                    {
                        await CimVmSettings.RemoveResourcesAsync(session, [connection], cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (connection is not null)
                {
                    await CimVmSettings.ModifyResourceAsync(session, connection, cancellationToken,
                        new CimXml.Property("HostResource", CimType.StringArray, new[] { CimVmSettings.SwitchPath(adapter.SwitchId) })).ConfigureAwait(false);
                }
                else
                {
                    using var template = CimVmSettings.Default(session, "Msvm_EthernetPortAllocationSettingData");
                    await CimVmSettings.AddResourceAsync(session, settings, template, cancellationToken,
                        new("Parent", CimType.String, CimVmSettings.ObjectPath(port.CimSystemProperties.ClassName, portId)),
                        new("HostResource", CimType.StringArray, new[] { CimVmSettings.SwitchPath(adapter.SwitchId) })).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            ports.Concat(connections).ToList().ForEach(item => item.Dispose());
        }
    }

    private static Task InvokeOnPlannedAsync(Guid plannedId, string method, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var planned = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_PlannedComputerSystem WHERE Name = '{plannedId:D}'")
                ?? throw new TransferException("The imported virtual machine is no longer waiting to be completed.");
            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection { CimMethodParameter.Create("PlannedSystem", planned, CimType.Reference, CimFlags.In) };
            using var result = await HyperVCim.InvokeAsync(session, service, method, parameters, method, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    private static CimInstance PlannedSettings(CimSession session, Guid plannedId) =>
        HyperVCim.QuerySingle(session,
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{plannedId:D}' AND VirtualSystemType = '{PlannedType}'")
        ?? throw new TransferException("The imported virtual machine is no longer waiting to be completed.");

    private static HashSet<Guid> PlannedIds(CimSession session)
    {
        var ids = new HashSet<Guid>();
        foreach (var planned in HyperVCim.Query(session, "SELECT Name FROM Msvm_PlannedComputerSystem"))
        {
            using (planned)
            {
                if (Guid.TryParse(planned.CimInstanceProperties["Name"]?.Value as string, out var id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }
}
