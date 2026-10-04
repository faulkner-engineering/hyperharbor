using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// <see cref="IHyperVStorage"/> through root\virtualization\v2.
/// Disks: Msvm_StorageAllocationSettingData with ResourceSubType "Virtual Hard Disk" (ISOs share ResourceType 31), grouped by the
/// Msvm_VirtualSystemSettingData (realized VM or checkpoint) whose InstanceID prefixes theirs.
/// Parents: Msvm_ImageManagementService.GetVirtualHardDiskSettingData.
/// Deletion: Msvm_VirtualSystemSnapshotService.DestroySnapshotTree and
/// Msvm_VirtualSystemManagementService.DestroySystem.
/// </summary>
public sealed class CimHyperVStorage : IHyperVStorage
{
    private const string RealizedType = "Microsoft:Hyper-V:System:Realized";
    private const string SnapshotTypePrefix = "Microsoft:Hyper-V:Snapshot:";
    // ISO and floppy images are ResourceType 31 too; only this subtype is a virtual hard disk.
    private const string VirtualHardDiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";
    private const string OpticalDiskSubType = "Microsoft:Hyper-V:Virtual CD/DVD Disk";

    public Task<StorageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session => Task.FromResult(Read(session)), cancellationToken);

    public Task<string?> GetParentPathAsync(string path, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_ImageManagementService")
                ?? throw new HyperVUnavailableException("The Hyper-V image management service was not found.");
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("Path", path, CimType.String, CimFlags.In),
            };

            CimMethodResult result;
            try
            {
                result = await HyperVCim.InvokeAsync(session, service, "GetVirtualHardDiskSettingData", parameters, "GetVirtualHardDiskSettingData", cancellationToken);
            }
            catch (Exception ex) when (ex is HyperVOperationException or HyperVJobFailedException)
            {
                // A missing or unreadable file ends the chain.
                return null;
            }

            if (result.OutParameters["SettingData"]?.Value is not string settingData)
            {
                return null;
            }

            return CimXml.ReadScalars(settingData).TryGetValue("ParentPath", out var parent) && !string.IsNullOrWhiteSpace(parent)
                ? parent
                : null;
        }, cancellationToken);

    public Task DeleteCheckpointsAsync(Guid vmId, Action<int> progress, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemSnapshotService")
                ?? throw new HyperVUnavailableException("The Hyper-V checkpoint service was not found.");

            // Deleting each tree from its root merges every checkpoint below it into the VM's disks.
            var snapshots = HyperVCim.Query(session,
                $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}'")
                .Where(settings => (settings.CimInstanceProperties["VirtualSystemType"]?.Value as string)?.StartsWith(SnapshotTypePrefix, StringComparison.Ordinal) == true)
                .ToList();
            var roots = snapshots.Where(snapshot => string.IsNullOrEmpty(snapshot.CimInstanceProperties["Parent"]?.Value as string)).ToList();

            for (var index = 0; index < roots.Count; index++)
            {
                var done = index;
                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("SnapshotSettingData", roots[index], CimType.Reference, CimFlags.In),
                };
                await HyperVCim.InvokeAsync(session, service, "DestroySnapshotTree", parameters, "DestroySnapshotTree", cancellationToken,
                    percent => progress((done * 100 + percent) / roots.Count));
            }

            foreach (var snapshot in snapshots)
            {
                snapshot.Dispose();
            }

            progress(100);
        }, cancellationToken);

    public Task DeleteVmAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.ManagementService(session);
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
                ?? throw new VmNotFoundException(vmId);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("AffectedSystem", system, CimType.Reference, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "DestroySystem", parameters, "DestroySystem", cancellationToken);
        }, cancellationToken);

    private static StorageSnapshot Read(CimSession session)
    {
        var names = new Dictionary<Guid, string>();
        foreach (var system in HyperVCim.Query(session, "SELECT Name, ElementName FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'"))
        {
            using (system)
            {
                if (Guid.TryParse(system.CimInstanceProperties["Name"]?.Value as string, out var id))
                {
                    names[id] = system.CimInstanceProperties["ElementName"]?.Value as string ?? id.ToString();
                }
            }
        }

        // Settings InstanceID "Microsoft:<guid>" -> (VM, is checkpoint).
        var owners = new Dictionary<string, (Guid VmId, bool InCheckpoint)>(StringComparer.OrdinalIgnoreCase);
        var checkpoints = new Dictionary<Guid, int>();
        foreach (var settings in HyperVCim.Query(session, "SELECT InstanceID, VirtualSystemIdentifier, VirtualSystemType FROM Msvm_VirtualSystemSettingData"))
        {
            using (settings)
            {
                var type = settings.CimInstanceProperties["VirtualSystemType"]?.Value as string;
                if (settings.CimInstanceProperties["InstanceID"]?.Value is not string instanceId
                    || !Guid.TryParse(settings.CimInstanceProperties["VirtualSystemIdentifier"]?.Value as string, out var vmId))
                {
                    continue;
                }

                var isCheckpoint = type?.StartsWith(SnapshotTypePrefix, StringComparison.Ordinal) == true;
                if (type == RealizedType || isCheckpoint)
                {
                    owners[instanceId] = (vmId, isCheckpoint);
                }

                if (isCheckpoint)
                {
                    checkpoints[vmId] = checkpoints.GetValueOrDefault(vmId) + 1;
                }
            }
        }

        var disks = Attachments(session, VirtualHardDiskSubType, owners, names);
        var images = Attachments(session, OpticalDiskSubType, owners, names);
        return new StorageSnapshot(disks, checkpoints, images);
    }

    /// <summary>Attachments of one storage subtype, each tied to its VM through the owning settings.</summary>
    private static List<DiskAttachment> Attachments(
        CimSession session,
        string subType,
        Dictionary<string, (Guid VmId, bool InCheckpoint)> owners,
        Dictionary<Guid, string> names)
    {
        var attachments = new List<DiskAttachment>();
        foreach (var storage in HyperVCim.Query(session, $"SELECT InstanceID, HostResource FROM Msvm_StorageAllocationSettingData WHERE ResourceSubType = '{subType}'"))
        {
            using (storage)
            {
                if (storage.CimInstanceProperties["InstanceID"]?.Value is not string instanceId
                    || storage.CimInstanceProperties["HostResource"]?.Value is not string[] { Length: > 0 } hostResource
                    || string.IsNullOrWhiteSpace(hostResource[0]))
                {
                    continue;
                }

                var separator = instanceId.IndexOf('\\', StringComparison.Ordinal);
                var settingsId = separator < 0 ? instanceId : instanceId[..separator];
                if (owners.TryGetValue(settingsId, out var owner))
                {
                    attachments.Add(new DiskAttachment(owner.VmId, names.GetValueOrDefault(owner.VmId, owner.VmId.ToString()), hostResource[0], owner.InCheckpoint));
                }
            }
        }

        return attachments;
    }
}
