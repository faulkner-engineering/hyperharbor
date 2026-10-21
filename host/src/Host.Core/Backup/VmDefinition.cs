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
        var gpus = CimVmSettings.Associated(session, settings, GpuClass);
        try
        {
            using var security = CimVmSettings.Associated(session, settings, "Msvm_SecuritySettingData").FirstOrDefault();
            var switchNames = HyperVCim.Query(session, "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch")
                .ToDictionary(item => (string)item.CimInstanceProperties["Name"].Value, item => (string)item.CimInstanceProperties["ElementName"].Value,
                    StringComparer.OrdinalIgnoreCase);

            var adapters = connections
                .Select(connection => connection.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts ? hosts[0] : null)
                .Select(path => path is null ? null : SwitchIdOf(path))
                .Select((id, index) => new VmAdapterDefinition(
                    id,
                    id is not null && switchNames.TryGetValue(id, out var name) ? name : null,
                    synthetic.ElementAtOrDefault(index)?.CimInstanceProperties["Address"]?.Value as string))
                .ToList();

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
            foreach (var instance in devices.Concat(storage).Concat(connections).Concat(synthetic).Concat(gpus))
            {
                instance.Dispose();
            }
        }
    }

    private static int Number(object? value) => value is null ? 0 : int.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);

    private static ulong Unsigned(CimInstance instance, string property) =>
        instance.CimInstanceProperties[property]?.Value is { } value ? Convert.ToUInt64(value, CultureInfo.InvariantCulture) : 0;

    // The key is preceded by "." or ",": a bare Name= would also match inside CreationClassName="Msvm_VirtualEthernetSwitch",
    // which Hyper-V writes first (seen live in TransferCimLiveTests).
    [GeneratedRegex("[.,]Name=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SwitchName();
}
