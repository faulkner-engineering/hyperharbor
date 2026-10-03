using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>Reading and changing a VM's settings objects through Msvm_VirtualSystemManagementService.</summary>
internal static class CimVmSettings
{
    public const string RealizedType = "Microsoft:Hyper-V:System:Realized";

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

    /// <summary>A WMI object path of the form Hyper-V stores in Parent and HostResource.</summary>
    public static string ObjectPath(string className, string instanceId) =>
        $@"\\{Environment.MachineName}\root\virtualization\v2:{className}.InstanceID=""{instanceId.Replace(@"\", @"\\", StringComparison.Ordinal)}""";

    public static string SwitchPath(string switchId) =>
        $@"\\{Environment.MachineName}\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch"",Name=""{switchId}""";
}
