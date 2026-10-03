using System.Reflection;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Host tag of docs/api.yaml.</summary>
public static class HostEndpoints
{
    public static IEndpointRouteBuilder MapHostEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ContractInfo.BasePath + "/host", GetHostInfo).WithName("getHostInfo");
        endpoints.MapGet(ContractInfo.BasePath + "/host/resources", GetResourcesAsync).WithName("getHostResources");
        endpoints.MapGet(ContractInfo.BasePath + "/isos", ListIsos).WithName("listIsos");
        endpoints.MapGet(ContractInfo.BasePath + "/switches", ListSwitchesAsync).WithName("listSwitches");
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

    private static async Task<Ok<HostResources>> GetResourcesAsync(
        IHostCapacityReader capacity,
        IHyperVHost hyperV,
        IOptions<LifecycleOptions> options,
        CancellationToken cancellationToken)
    {
        var host = capacity.Read();
        var settings = options.Value;
        var diskFolder = string.IsNullOrWhiteSpace(settings.VmRootFolder)
            ? (await hyperV.GetDefaultsAsync(cancellationToken)).VirtualHardDiskFolder
            : Path.GetFullPath(settings.VmRootFolder);
        return TypedResults.Ok(new HostResources(
            host.LogicalProcessorCount,
            host.TotalMemoryMb,
            host.AvailableMemoryMb,
            settings.HostMemoryReserveMb,
            diskFolder,
            settings.EffectiveIsoFolder));
    }

    private static Ok<IReadOnlyList<IsoImage>> ListIsos(IsoLibrary library) => TypedResults.Ok(library.List());

    private static async Task<Ok<IReadOnlyList<VirtualSwitch>>> ListSwitchesAsync(IHyperVHost hyperV, CancellationToken cancellationToken) =>
        TypedResults.Ok(await hyperV.ListSwitchesAsync(cancellationToken));
}
