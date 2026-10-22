using System.Globalization;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Lifecycle;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Backup;

public sealed record VmAdapterDefinition(string? SwitchId, string? SwitchName, string? MacAddress);

/// <param name="ControllerType">"scsi" or "ide".</param>
public sealed record VmDiskDefinition(string Path, string ControllerType, int ControllerNumber, int Lun)
{
    /// <summary>A checkpoint's differencing disk: the VM's current disk sits on top of a chain.</summary>
    public bool IsDifferencing => DiskPaths.IsCheckpointDisk(Path);
}

/// <summary>The GPU partition adapter's values in Hyper-V's relative units (see Performance mode).</summary>
public sealed record GpuPartitionDefinition(ulong Vram, ulong Encode, ulong Decode, ulong Compute);

/// <summary>Everything about a VM's hardware that a restore needs to define it again.</summary>
public sealed record VmDefinition(
    Guid VmId,
    string Name,
    int Generation,
    string? Notes,
    ComputeState Compute,
    bool SecureBoot,
    string? SecureBootTemplateId,
    bool TpmEnabled,
    IReadOnlyList<VmAdapterDefinition> Adapters,
    IReadOnlyList<VmDiskDefinition> Disks,
    GpuPartitionDefinition? Gpu,
    int LowMmioGapMb,
    int HighMmioGapMb)
{
    public bool HasGpuPartition => Gpu is not null;
}

public interface IHyperVDefinitionReader
{
    /// <exception cref="VmNotFoundException">The VM does not exist.</exception>
    Task<VmDefinition> ReadAsync(Guid vmId, CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVDefinitionReader"/> through root\virtualization\v2.</summary>
public sealed partial class CimHyperVDefinitionReader(IHyperVCompute compute) : IHyperVDefinitionReader
{
    private const string DiskSubType = "Microsoft:Hyper-V:Virtual Hard Disk";
    private const string DriveSubType = "Microsoft:Hyper-V:Synthetic Disk Drive";
    private const string ScsiSubType = "Microsoft:Hyper-V:Synthetic SCSI Controller";
    private const string IdeSubType = "Microsoft:Hyper-V:Emulated IDE Controller";
    private const string GpuClass = "Msvm_GpuPartitionSettingData";

    public async Task<VmDefinition> ReadAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var state = await compute.ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        return await HyperVCim.RunAsync(session => Task.FromResult(Read(session, vmId, state)), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The switch ID inside a Hyper-V object path such as ...Msvm_VirtualEthernetSwitch...,Name="GUID".</summary>
    internal static string? SwitchIdOf(string objectPath) =>
        SwitchName().Match(objectPath) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>Whether a Parent path names the settings object with this InstanceID (paths double their backslashes).</summary>
    internal static bool ParentMatches(string? parentPath, string instanceId) =>
        parentPath is not null && parentPath.Replace(@"\\", @"\", StringComparison.Ordinal)
            .Contains($"InstanceID=\"{instanceId}\"", StringComparison.OrdinalIgnoreCase);

    private static VmDefinition Read(CimSession session, Guid vmId, ComputeState state)
    {
        using var settings = CimVmSettings.Realized(session, vmId);
        var devices = CimVmSettings.Associated(session, settings, "Msvm_ResourceAllocationSettingData");
        var storage = CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData");
        var connections = CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData");
        var synthetic = CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData");
        var emulated = CimVmSettings.Associated(session, settings, "Msvm_EmulatedEthernetPortSettingData");
        var gpus = CimVmSettings.Associated(session, settings, GpuClass);
        try
        {
            using var security = CimVmSettings.Associated(session, settings, "Msvm_SecuritySettingData").FirstOrDefault();
            var switchNames = HyperVCim.Query(session, "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch")
                .ToDictionary(item => (string)item.CimInstanceProperties["Name"].Value, item => (string)item.CimInstanceProperties["ElementName"].Value,
                    StringComparer.OrdinalIgnoreCase);

            var adapters = Adapters(
                synthetic.Concat(emulated)
                    .Select(port => new AdapterPort((string)port.CimInstanceProperties["InstanceID"].Value, port.CimInstanceProperties["Address"]?.Value as string))
                    .ToList(),
                connections
                    .Select(connection => new AdapterConnection(
                        connection.CimInstanceProperties["Parent"]?.Value as string,
                        connection.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts ? hosts[0] : null))
                    .ToList(),
                switchNames);

            var disks = new List<VmDiskDefinition>();
            foreach (var disk in storage.Where(item => item.CimInstanceProperties["ResourceSubType"]?.Value as string == DiskSubType))
            {
                var path = disk.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts ? hosts[0] : null;
                if (string.IsNullOrEmpty(path))
                {
                    continue;
                }

                var drive = devices.FirstOrDefault(item =>
                    item.CimInstanceProperties["ResourceSubType"]?.Value as string == DriveSubType
                    && ParentMatches(disk.CimInstanceProperties["Parent"]?.Value as string, (string)item.CimInstanceProperties["InstanceID"].Value));
                var controller = drive is null ? null : devices.FirstOrDefault(item =>
                    item.CimInstanceProperties["ResourceSubType"]?.Value as string is ScsiSubType or IdeSubType
                    && ParentMatches(drive.CimInstanceProperties["Parent"]?.Value as string, (string)item.CimInstanceProperties["InstanceID"].Value));
                disks.Add(new VmDiskDefinition(
                    DiskPaths.Normalize(path),
                    controller?.CimInstanceProperties["ResourceSubType"]?.Value as string == IdeSubType ? "ide" : "scsi",
                    Number(controller?.CimInstanceProperties["Address"]?.Value),
                    Number(drive?.CimInstanceProperties["AddressOnParent"]?.Value)));
            }

            var gpu = gpus.FirstOrDefault() is { } partition
                ? new GpuPartitionDefinition(
                    Unsigned(partition, "MaxPartitionVRAM"), Unsigned(partition, "MaxPartitionEncode"),
                    Unsigned(partition, "MaxPartitionDecode"), Unsigned(partition, "MaxPartitionCompute"))
                : null;

            var notes = (settings.CimInstanceProperties["Notes"]?.Value as string[] ?? []).Where(line => !string.IsNullOrEmpty(line)).ToArray();
            return new VmDefinition(
                vmId,
                (string)settings.CimInstanceProperties["ElementName"].Value,
                (settings.CimInstanceProperties["VirtualSystemSubType"]?.Value as string) == "Microsoft:Hyper-V:SubType:2" ? 2 : 1,
                notes.Length == 0 ? null : string.Join('\n', notes),
                state,
                settings.CimInstanceProperties["SecureBootEnabled"]?.Value is true,
                settings.CimInstanceProperties["SecureBootTemplateId"]?.Value as string,
                security?.CimInstanceProperties["TpmEnabled"]?.Value is true,
                adapters,
                disks.OrderBy(disk => disk.ControllerNumber).ThenBy(disk => disk.Lun).ToList(),
                gpu,
                (int)Unsigned(settings, "LowMmioGapSize"),
                (int)Unsigned(settings, "HighMmioGapSize"));
        }
        finally
        {
            foreach (var instance in devices.Concat(storage).Concat(connections).Concat(synthetic).Concat(emulated).Concat(gpus))
            {
                instance.Dispose();
            }
        }
    }

    internal sealed record AdapterPort(string InstanceId, string? Address);

    /// <param name="Parent">The object path of the adapter the connection belongs to.</param>
    /// <param name="HostResource">The switch's object path, or null when the connection names none.</param>
    internal sealed record AdapterConnection(string? Parent, string? HostResource);

    /// <summary>
    /// One entry per network adapter, in the order given (synthetic adapters first, in Hyper-V's order, which a transfer
    /// matches by position). An adapter's switch comes from the connection whose Parent names it; an adapter connected to
    /// nothing has no connection object at all, so it was dropped when the list was built from the connections (seen
    /// live: a VM copied with an unconnected adapter could not be moved on, because its preview listed no adapters).
    /// </summary>
    internal static IReadOnlyList<VmAdapterDefinition> Adapters(
        IReadOnlyList<AdapterPort> ports,
        IReadOnlyList<AdapterConnection> connections,
        IReadOnlyDictionary<string, string> switchNames) =>
        ports.Select(port =>
        {
            var host = connections.FirstOrDefault(connection => ParentMatches(connection.Parent, port.InstanceId))?.HostResource;
            var id = host is null ? null : SwitchIdOf(host);
            return new VmAdapterDefinition(id, id is not null && switchNames.TryGetValue(id, out var name) ? name : null, port.Address);
        }).ToList();

    private static int Number(object? value) => value is null ? 0 : int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static ulong Unsigned(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : 0;

    // The key is preceded by "." or ",": a bare Name= would also match inside CreationClassName="Msvm_VirtualEthernetSwitch",
    // which Hyper-V writes first (seen live in TransferCimLiveTests).
    [GeneratedRegex("[.,]Name=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SwitchName();
}
