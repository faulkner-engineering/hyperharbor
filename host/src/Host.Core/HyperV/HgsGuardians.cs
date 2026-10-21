using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>The Host Guardian Service client classes (root\Microsoft\Windows\Hgs) that the HgsClient cmdlets wrap.</summary>
internal static class HgsGuardians
{
    public const string Namespace = @"root\Microsoft\Windows\Hgs";

    /// <summary>The guardian Hyper-V Manager uses for a VM's TPM on a host that is not guarded.</summary>
    public const string LocalGuardianName = "UntrustedGuardian";

    public static CimInstance? Find(CimSession session, string name) =>
        session.QueryInstances(Namespace, HyperVCim.QueryDialect, $"SELECT * FROM MSFT_HgsGuardian WHERE Name = '{HyperVCim.Escape(name)}'").FirstOrDefault();

    /// <summary>The local "UntrustedGuardian", created with self-signed certificates if missing, as Hyper-V Manager does.</summary>
    public static CimInstance LocalGuardian(CimSession session)
    {
        if (Find(session, LocalGuardianName) is { } existing)
        {
            return existing;
        }

        var create = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("Name", LocalGuardianName, CimType.String, CimFlags.In),
            CimMethodParameter.Create("GenerateCertificates", true, CimType.Boolean, CimFlags.In),
        };
        using var created = session.InvokeMethod(Namespace, "MSFT_HgsGuardian", "NewByGenerateCertificates", create);
        return created.OutParameters["cmdletOutput"]?.Value as CimInstance
            ?? throw new HyperVOperationException("creating the local key protector guardian", HyperVCim.ReturnCode(created));
    }

    /// <summary>The CmdletOutput of a static HGS method, or an error naming the operation.</summary>
    public static CimInstance Output(CimMethodResult result, string operation) =>
        result.OutParameters["cmdletOutput"]?.Value as CimInstance
            ?? throw new HyperVOperationException(operation, ReturnCode(result));

    /// <summary>HGS methods return a signed integer, which <see cref="HyperVCim.ReturnCode"/> would not convert.</summary>
    public static uint ReturnCode(CimMethodResult result) =>
        unchecked((uint)Convert.ToInt64(result.ReturnValue?.Value ?? -1L, System.Globalization.CultureInfo.InvariantCulture));
}
