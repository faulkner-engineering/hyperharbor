using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.HyperV;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>A VM's compute settings as Hyper-V stores them.</summary>
/// <param name="NetworkAdapterCount">Network adapters connected to a switch.</param>
/// <param name="SwitchId">The switch of the first connected network adapter; null when none is connected.</param>
/// <param name="AdapterCount">Network adapters, connected or not.</param>
public sealed record ComputeState(
    int ProcessorCount,
    long StartupMemoryMb,
    long MaximumMemoryMb,
    bool DynamicMemory,
    bool NestedVirtualization,
    bool MacAddressSpoofing,
    int NetworkAdapterCount,
    string? SwitchId = null,
    int AdapterCount = 0);

/// <summary>Settings to change; null leaves a setting as it is.</summary>
/// <param name="SwitchId">A switch that every network adapter connects to.</param>
public sealed record ComputeChange(
    int? ProcessorCount = null,
    long? StartupMemoryMb = null,
    long? MaximumMemoryMb = null,
    bool? DynamicMemory = null,
    bool? NestedVirtualization = null,
    bool? MacAddressSpoofing = null,
    string? SwitchId = null)
{
    public bool ChangesMemory => StartupMemoryMb is not null || MaximumMemoryMb is not null || DynamicMemory is not null;

    public bool ChangesProcessor => ProcessorCount is not null || NestedVirtualization is not null;
}

public interface IHyperVCompute
{
    Task<ComputeState> ReadAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>Applies <paramref name="change"/>. Memory changes are applied with the merged result in <paramref name="desired"/>.</summary>
    Task ApplyAsync(Guid vmId, ComputeChange change, ComputeState desired, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IHyperVCompute"/> through root\virtualization\v2: Msvm_ProcessorSettingData (VirtualQuantity,
/// ExposeVirtualizationExtensions), Msvm_MemorySettingData, Msvm_EthernetSwitchPortSecuritySettingData
/// (AllowMacSpoofing) on each switch connection, and each network adapter's switch connection (HostResource).
/// </summary>
public sealed class CimHyperVCompute : IHyperVCompute
{
    public Task<ComputeState> ReadAsync(Guid vmId, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using var processor = CimVmSettings.AssociatedSingle(session, settings, "Msvm_ProcessorSettingData");
            using var memory = CimVmSettings.AssociatedSingle(session, settings, "Msvm_MemorySettingData");
            var connections = Connections(session, settings);
            var spoofing = connections.Count > 0 && connections.All(connection =>
            {
                using var security = SecurityOf(session, connection);
                return security?.CimInstanceProperties["AllowMacSpoofing"]?.Value is true;
            });
            var ports = Ports(session, settings);
            var switchId = ports
                .Select(port => ConnectionOf(connections, port) is { } connection && CimVmSettings.ConnectedSwitchPath(connection) is { } path
                    ? CimHyperVDefinitionReader.SwitchIdOf(path)
                    : null)
                .FirstOrDefault(id => id is not null);
            foreach (var item in connections.Concat(ports))
            {
                item.Dispose();
            }

            return Task.FromResult(new ComputeState(
                (int)Convert.ToUInt64(processor.CimInstanceProperties["VirtualQuantity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                (long)Convert.ToUInt64(memory.CimInstanceProperties["VirtualQuantity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                (long)Convert.ToUInt64(memory.CimInstanceProperties["Limit"].Value, System.Globalization.CultureInfo.InvariantCulture),
                memory.CimInstanceProperties["DynamicMemoryEnabled"]?.Value is true,
                processor.CimInstanceProperties["ExposeVirtualizationExtensions"]?.Value is true,
                spoofing,
                connections.Count,
                switchId,
                ports.Count));
        }, cancellationToken);

    public Task ApplyAsync(Guid vmId, ComputeChange change, ComputeState desired, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);

            if (change.ChangesProcessor)
            {
                using var processor = CimVmSettings.AssociatedSingle(session, settings, "Msvm_ProcessorSettingData");
                var changes = new List<CimXml.Property>();
                if (change.ProcessorCount is { } count)
                {
                    changes.Add(new("VirtualQuantity", CimType.UInt64, (ulong)count));
                }

                if (change.NestedVirtualization is { } nested)
                {
                    changes.Add(new("ExposeVirtualizationExtensions", CimType.Boolean, nested));
                }

                await CimVmSettings.ModifyResourceAsync(session, processor, cancellationToken, [.. changes]);
            }

            if (change.ChangesMemory)
            {
                using var memory = CimVmSettings.AssociatedSingle(session, settings, "Msvm_MemorySettingData");
                await CimVmSettings.ModifyResourceAsync(session, memory, cancellationToken,
                    CimHyperVBuilder.MemoryChanges(desired.StartupMemoryMb, desired.MaximumMemoryMb, desired.DynamicMemory));
            }

            if (change.MacAddressSpoofing is { } allow)
            {
                foreach (var connection in Connections(session, settings))
                {
                    using (connection)
                    {
                        await SetMacSpoofingAsync(session, connection, allow, cancellationToken);
                    }
                }
            }

            if (change.SwitchId is { } switchId)
            {
                var ports = Ports(session, settings);
                var connections = CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData");
                try
                {
                    foreach (var port in ports)
                    {
                        await CimVmSettings.ConnectAdapterAsync(session, settings, port, ConnectionOf(connections, port), switchId, cancellationToken);
                    }
                }
                finally
                {
                    ports.Concat(connections).ToList().ForEach(item => item.Dispose());
                }
            }
        }, cancellationToken);

    /// <summary>The VM's network adapters: synthetic ones first, then legacy ones.</summary>
    private static List<CimInstance> Ports(CimSession session, CimInstance settings) =>
        [
            .. CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData"),
            .. CimVmSettings.Associated(session, settings, "Msvm_EmulatedEthernetPortSettingData"),
        ];

    /// <summary>The switch connection whose Parent is <paramref name="port"/>, if it has one.</summary>
    private static CimInstance? ConnectionOf(IEnumerable<CimInstance> connections, CimInstance port) =>
        connections.FirstOrDefault(connection => CimHyperVDefinitionReader.ParentMatches(
            connection.CimInstanceProperties["Parent"]?.Value as string, (string)port.CimInstanceProperties["InstanceID"].Value));

    /// <summary>Switch connections (Msvm_EthernetPortAllocationSettingData) that point at a switch and are not disconnected.</summary>
    private static List<CimInstance> Connections(CimSession session, CimInstance settings)
    {
        var connected = new List<CimInstance>();
        foreach (var connection in CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData"))
        {
            if (CimVmSettings.ConnectedSwitchPath(connection) is not null)
            {
                connected.Add(connection);
            }
            else
            {
                connection.Dispose();
            }
        }

        return connected;
    }

    private static CimInstance? SecurityOf(CimSession session, CimInstance connection) =>
        CimVmSettings.Associated(session, connection, "Msvm_EthernetSwitchPortSecuritySettingData").FirstOrDefault();

    private static async Task SetMacSpoofingAsync(CimSession session, CimInstance connection, bool allow, CancellationToken cancellationToken)
    {
        using var service = HyperVCim.ManagementService(session);
        var change = new CimXml.Property("AllowMacSpoofing", CimType.Boolean, allow);
        using var existing = SecurityOf(session, connection);
        if (existing is not null)
        {
            var modify = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("FeatureSettings", new[] { CimXml.Write(existing, change) }, CimType.StringArray, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "ModifyFeatureSettings", modify, "ModifyFeatureSettings", cancellationToken);
            return;
        }

        using var template = CimVmSettings.Default(session, "Msvm_EthernetSwitchPortSecuritySettingData");
        var add = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("AffectedConfiguration", connection, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create("FeatureSettings", new[] { CimXml.Write(template, change) }, CimType.StringArray, CimFlags.In),
        };
        await HyperVCim.InvokeAsync(session, service, "AddFeatureSettings", add, "AddFeatureSettings", cancellationToken);
    }
}
