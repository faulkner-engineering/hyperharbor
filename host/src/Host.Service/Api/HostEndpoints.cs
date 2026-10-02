using System.Reflection;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Host tag of docs/api.yaml.</summary>
public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ContractInfo.BasePath + "/host", GetHostInfo).WithName("getHostInfo");
        return endpoints;
    }

    /// <summary>Host service version without build metadata.</summary>
    public static string HostVersion =>
        typeof(HostEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static Ok<HostInfo> GetHostInfo(HostIdentityStore identity, HostCertificateStore certificate)
    {
        return TypedResults.Ok(new HostInfo(
            identity.GetOrCreateHostId(),
            Environment.MachineName,
            HostVersion,
            ContractInfo.ApiVersion,
            CertificateFingerprint.Of(certificate.GetOrCreate())));
    }
}
