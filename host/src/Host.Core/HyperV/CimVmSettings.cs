using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>Reading and changing a VM's settings objects through Msvm_VirtualSystemManagementService.</summary>
internal static class CimVmSettings
{
    public const string RealizedType = "Microsoft:Hyper-V:System:Realized";

    /// <summary>EnabledState of a switch connection, which Disconnect-VMNetworkAdapter (or Hyper-V Manager) sets to disabled.</summary>
    private const ushort ConnectionEnabled = 2;

    private const ushort ConnectionDisabled = 3;

    /// <summary>The VM's current (not checkpoint) Msvm_VirtualSystemSettingData.</summary>
    public static CimInstance Realized(CimSession session, Guid vmId) =>
        HyperVCim.QuerySingle(session,
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = '{RealizedType}'")
        ?? throw new VmNotFoundException(vmId);

    /// <summary>Settings objects of <paramref name="className"/> that belong to <paramref name="settings"/>.</summary>
    public static List<CimInstance> Associated(CimSession session, CimInstance settings, string className) =>
        session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, className, null, null).ToList();

    public static CimInstance AssociatedSingle(CimSession session, CimInstance settings, string className)
    {
        var instances = Associated(session, settings, className);
        foreach (var extra in instances.Skip(1))
        {
            extra.Dispose();
        }

        return instances.FirstOrDefault() ?? throw new HyperVOperationException($"reading {className}", uint.MaxValue);
    }

    /// <summary>The default (template) instance Hyper-V uses for a new device of this subtype.</summary>
    public static CimInstance Default(CimSession session, string className, string? resourceSubType = null) =>
        HyperVCim.QuerySingle(session,
            $"SELECT * FROM {className} WHERE InstanceID LIKE '%Default'"
            + (resourceSubType is null ? string.Empty : $" AND ResourceSubType = '{HyperVCim.Escape(resourceSubType)}'"))
        ?? throw new HyperVOperationException($"finding the default {resourceSubType ?? className}", uint.MaxValue);

    /// <summary>Changes existing device settings (ModifyResourceSettings).</summary>
    public static Task ModifyResourceAsync(CimSession session, CimInstance resource, CancellationToken cancellationToken, params CimXml.Property[] changes)
    {
        using var service = HyperVCim.ManagementService(session);
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("ResourceSettings", new[] { CimXml.Write(resource, changes) }, CimType.StringArray, CimFlags.In),
        };
        return HyperVCim.InvokeAsync(session, service, "ModifyResourceSettings", parameters, "ModifyResourceSettings", cancellationToken);
    }

    /// <summary>Changes VM-level settings (ModifySystemSettings).</summary>
    public static Task ModifySystemAsync(CimSession session, CimInstance settings, CancellationToken cancellationToken, params CimXml.Property[] changes)
    {
        using var service = HyperVCim.ManagementService(session);
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("SystemSettings", CimXml.Write(settings, changes), CimType.String, CimFlags.In),
        };
        return HyperVCim.InvokeAsync(session, service, "ModifySystemSettings", parameters, "ModifySystemSettings", cancellationToken);
    }

    /// <summary>Adds a device to the VM and returns the WMI object path of the new settings object.</summary>
    public static async Task<string> AddResourceAsync(CimSession session, CimInstance systemSettings, CimInstance template, CancellationToken cancellationToken, params CimXml.Property[] changes)
    {
        using var service = HyperVCim.ManagementService(session);
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("AffectedConfiguration", systemSettings, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create("ResourceSettings", new[] { CimXml.Write(template, changes) }, CimType.StringArray, CimFlags.In),
        };
        var result = await HyperVCim.InvokeAsync(session, service, "AddResourceSettings", parameters, "AddResourceSettings", cancellationToken).ConfigureAwait(false);
        var added = (result.OutParameters["ResultingResourceSettings"]?.Value as CimInstance[])?.FirstOrDefault()
            ?? throw new HyperVOperationException("AddResourceSettings (no resulting settings)", uint.MaxValue);
        return ObjectPath(added.CimSystemProperties.ClassName, (string)added.CimInstanceProperties["InstanceID"].Value);
    }

    /// <summary>Removes devices from the VM (RemoveResourceSettings).</summary>
    public static async Task RemoveResourcesAsync(CimSession session, IReadOnlyList<CimInstance> resources, CancellationToken cancellationToken)
    {
        using var service = HyperVCim.ManagementService(session);
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("ResourceSettings", resources.ToArray(), CimType.ReferenceArray, CimFlags.In),
        };
        using var result = await HyperVCim.InvokeAsync(session, service, "RemoveResourceSettings", parameters, "RemoveResourceSettings", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A WMI object path of the form Hyper-V stores in Parent and HostResource.</summary>
    public static string ObjectPath(string className, string instanceId) =>
        $@"\\{Environment.MachineName}\root\virtualization\v2:{className}.InstanceID=""{instanceId.Replace(@"\", @"\\", StringComparison.Ordinal)}""";

    public static string SwitchPath(string switchId) =>
        $@"\\{Environment.MachineName}\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch"",Name=""{switchId}""";

    /// <summary>
    /// The switch path a connection (Msvm_EthernetPortAllocationSettingData) points at, or null when it names none or is
    /// disconnected: a disconnected adapter keeps its connection and its switch path, with EnabledState 3 (seen live).
    /// </summary>
    public static string? ConnectedSwitchPath(CimInstance connection)
    {
        if (connection.CimInstanceProperties["EnabledState"]?.Value is { } state
            && Convert.ToUInt16(state, System.Globalization.CultureInfo.InvariantCulture) == ConnectionDisabled)
        {
            return null;
        }

        return connection.CimInstanceProperties["HostResource"]?.Value is string[] { Length: > 0 } hosts && !string.IsNullOrEmpty(hosts[0]) ? hosts[0] : null;
    }

    /// <summary>
    /// Connects a network adapter (<paramref name="port"/>) to a switch, or disconnects it when <paramref name="switchId"/>
    /// is null. <paramref name="connection"/> is the adapter's Msvm_EthernetPortAllocationSettingData, if it has one: it is
    /// pointed at the switch and enabled (a disconnected adapter keeps a disabled one), or removed; an adapter without one
    /// gets a new one. Works on a running VM.
    /// </summary>
    public static async Task ConnectAdapterAsync(
        CimSession session, CimInstance settings, CimInstance port, CimInstance? connection, string? switchId, CancellationToken cancellationToken)
    {
        if (switchId is null)
        {
            if (connection is not null)
            {
                await RemoveResourcesAsync(session, [connection], cancellationToken).ConfigureAwait(false);
            }
        }
        else if (connection is not null)
        {
            await ModifyResourceAsync(session, connection, cancellationToken,
                new CimXml.Property("HostResource", CimType.StringArray, new[] { SwitchPath(switchId) }),
                new CimXml.Property("EnabledState", CimType.UInt16, ConnectionEnabled)).ConfigureAwait(false);
        }
        else
        {
            using var template = Default(session, "Msvm_EthernetPortAllocationSettingData");
            await AddResourceAsync(session, settings, template, cancellationToken,
                new("Parent", CimType.String, ObjectPath(port.CimSystemProperties.ClassName, (string)port.CimInstanceProperties["InstanceID"].Value)),
                new("HostResource", CimType.StringArray, new[] { SwitchPath(switchId) })).ConfigureAwait(false);
        }
    }
}
