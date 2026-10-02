using Microsoft.Extensions.Logging;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Reads virtual machine data from the local root\virtualization\v2 CIM namespace. Read-only.
/// </summary>
public sealed class CimHyperVReader : IHyperVReader
{
    private const string Namespace = @"root\virtualization\v2";
    private const string QueryDialect = "WQL";

    // RequestedInformation codes for Msvm_VirtualSystemManagementService.GetSummaryInformation.
    // Only requested fields are populated, so Name is required to match rows to virtual machines.
    private const uint NameCode = 0;
    private const uint ProcessorLoadCode = 101;
    private const uint MemoryUsageCode = 103;

    private readonly ILogger<CimHyperVReader> _logger;

    public CimHyperVReader(ILogger<CimHyperVReader> logger)
    {
        _logger = logger;
    }

    public Task<HyperVSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        return Task.Run(() => ReadSnapshot(cancellationToken), cancellationToken);
    }

    private HyperVSnapshot ReadSnapshot(CancellationToken cancellationToken)
    {
        using var session = CimSession.Create(null);
        List<CimInstance> settingInstances = [];

        try
        {
            var systems = ReadComputerSystems(session);
            cancellationToken.ThrowIfCancellationRequested();

            settingInstances = Query(session,
                "SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'");
            var settings = settingInstances
                .Select(instance => TryParseGuid(GetString(instance, "VirtualSystemIdentifier")) is { } vmId
                    ? new SettingsRow(vmId, GetString(instance, "VirtualSystemSubType"))
                    : null)
                .OfType<SettingsRow>()
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();

            var summaries = ReadSummaries(session, settingInstances);
            cancellationToken.ThrowIfCancellationRequested();

            var guestNetworks = ReadGuestNetworks(session);

            return new HyperVSnapshot(systems, settings, summaries, guestNetworks);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.InvalidNamespace)
        {
            throw new HyperVUnavailableException(
                "The Hyper-V management namespace was not found. Enable the Hyper-V role on this host.", ex);
        }
        catch (CimException ex) when (ex.NativeErrorCode is NativeErrorCode.AccessDenied)
        {
            throw new HyperVUnavailableException(
                "Access to Hyper-V was denied. Run elevated or add the account to the Hyper-V Administrators group.", ex);
        }
        finally
        {
            foreach (var instance in settingInstances)
            {
                instance.Dispose();
            }
        }
    }

    private static List<ComputerSystemRow> ReadComputerSystems(CimSession session)
    {
        var rows = new List<ComputerSystemRow>();
        foreach (var instance in Query(session, "SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'"))
        {
            using (instance)
            {
                if (TryParseGuid(GetString(instance, "Name")) is not { } id)
                {
                    continue;
                }

                var operationalStatus = instance.CimInstanceProperties["OperationalStatus"]?.Value as ushort[];

                rows.Add(new ComputerSystemRow(
                    id,
                    GetString(instance, "ElementName") ?? id.ToString(),
                    GetNullable<ushort>(instance, "EnabledState") ?? 0,
                    GetNullable<ulong>(instance, "OnTimeInMilliseconds") ?? 0,
                    operationalStatus is { Length: > 0 } ? operationalStatus[0] : null,
                    GetNullable<ushort>(instance, "RequestedState") ?? ComputerSystemRow.RequestedStateNoChange,
                    GetString(instance, "OtherEnabledState")));
            }
        }

        return rows;
    }

    /// <summary>
    /// Reads processor load and memory usage. Failures are logged and produce no rows, because these
    /// values are optional in the contract.
    /// </summary>
    private List<SummaryRow> ReadSummaries(CimSession session, List<CimInstance> settingInstances)
    {
        if (settingInstances.Count == 0)
        {
            return [];
        }

        try
        {
            var services = Query(session, "SELECT * FROM Msvm_VirtualSystemManagementService");
            using var service = services.FirstOrDefault();
            if (service is null)
            {
                _logger.LogWarning("Msvm_VirtualSystemManagementService was not found; CPU and memory usage are unavailable.");
                return [];
            }

            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SettingData", settingInstances.ToArray(), CimType.ReferenceArray, CimFlags.In),
                CimMethodParameter.Create("RequestedInformation", new[] { NameCode, ProcessorLoadCode, MemoryUsageCode }, CimType.UInt32Array, CimFlags.In),
            };

            using var result = session.InvokeMethod(Namespace, service, "GetSummaryInformation", parameters);
            if (result.OutParameters["SummaryInformation"]?.Value is not CimInstance[] summaries)
            {
                return [];
            }

            var rows = new List<SummaryRow>();
            foreach (var summary in summaries)
            {
                using (summary)
                {
                    if (TryParseGuid(GetString(summary, "Name")) is { } vmId)
                    {
                        rows.Add(new SummaryRow(
                            vmId,
                            GetNullable<ushort>(summary, "ProcessorLoad"),
                            GetNullable<ulong>(summary, "MemoryUsage")));
                    }
                }
            }

            return rows;
        }
        catch (CimException ex)
        {
            _logger.LogWarning(ex, "GetSummaryInformation failed; CPU and memory usage are unavailable.");
            return [];
        }
    }

    private static List<GuestNetworkRow> ReadGuestNetworks(CimSession session)
    {
        var rows = new List<GuestNetworkRow>();
        foreach (var instance in Query(session, "SELECT InstanceID, IPAddresses FROM Msvm_GuestNetworkAdapterConfiguration"))
        {
            using (instance)
            {
                // InstanceID format: Microsoft:GuestNetwork\<VM GUID>\<adapter ID>
                var segments = GetString(instance, "InstanceID")?.Split('\\') ?? [];
                if (segments.Length < 2 || TryParseGuid(segments[1]) is not { } vmId)
                {
                    continue;
                }

                var addresses = instance.CimInstanceProperties["IPAddresses"]?.Value as string[] ?? [];
                rows.Add(new GuestNetworkRow(vmId, addresses));
            }
        }

        return rows;
    }

    private static List<CimInstance> Query(CimSession session, string query)
    {
        return session.QueryInstances(Namespace, QueryDialect, query).ToList();
    }

    private static string? GetString(CimInstance instance, string propertyName)
    {
        return instance.CimInstanceProperties[propertyName]?.Value as string;
    }

    private static T? GetNullable<T>(CimInstance instance, string propertyName)
        where T : struct
    {
        var value = instance.CimInstanceProperties[propertyName]?.Value;
        return value is null ? null : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Guid? TryParseGuid(string? value)
    {
        return Guid.TryParse(value, out var guid) ? guid : null;
    }
}
