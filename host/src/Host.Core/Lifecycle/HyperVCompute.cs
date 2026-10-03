using HyperHarbor.Host.Core.HyperV;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>A VM's compute settings as Hyper-V stores them.</summary>
public sealed record ComputeState(
    int ProcessorCount,
    long StartupMemoryMb,
    long MaximumMemoryMb,
    bool DynamicMemory,
    bool NestedVirtualization,
    bool MacAddressSpoofing,
    int NetworkAdapterCount);

/// <summary>Settings to change; null leaves a setting as it is.</summary>
public sealed record ComputeChange(
    int? ProcessorCount = null,
    long? StartupMemoryMb = null,
    long? MaximumMemoryMb = null,
    bool? DynamicMemory = null,
    bool? NestedVirtualization = null,
    bool? MacAddressSpoofing = null)
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
/// ExposeVirtualizationExtensions), Msvm_MemorySettingData, and Msvm_EthernetSwitchPortSecuritySettingData
/// (AllowMacSpoofing) on each switch connection.
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
            foreach (var connection in connections)
            {
                connection.Dispose();
            }

            return Task.FromResult(new ComputeState(
                (int)Convert.ToUInt64(processor.CimInstanceProperties["VirtualQuantity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                (long)Convert.ToUInt64(memory.CimInstanceProperties["VirtualQuantity"].Value, System.Globalization.CultureInfo.InvariantCulture),
                (long)Convert.ToUInt64(memory.CimInstanceProperties["Limit"].Value, System.Globalization.CultureInfo.InvariantCulture),
                memory.CimInstanceProperties["DynamicMemoryEnabled"]?.Value is true,
                processor.CimInstanceProperties["ExposeVirtualizationExtensions"]?.Value is true,
                spoofing,
                connections.Count));
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
        }, cancellationToken);

    /// <summary>Switch connections (Msvm_EthernetPortAllocationSettingData) that point at a switch.</summary>
    private static List<CimInstance> Connections(CimSession session, CimInstance settings) =>
        CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData")
            .Where(connection => connection.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts && !string.IsNullOrEmpty(hosts[0]))
            .ToList();

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
