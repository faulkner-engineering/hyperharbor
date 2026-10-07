using System.Reflection;
using HyperHarbor.Host.Core.Diagnostics;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.RemoteDesktop;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.AspNetCore.Http.Features;
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
        endpoints.MapGet(ContractInfo.BasePath + "/host/remote-desktop", GetRemoteDesktop).WithName("getHostRemoteDesktop");
        endpoints.MapPost(ContractInfo.BasePath + "/host/remote-desktop/enable", EnableRemoteDesktop).WithName("enableHostRemoteDesktop")
            .Audited()
            .RequireElevation();
        endpoints.MapPost(ContractInfo.BasePath + "/host/logs/bundle", DownloadLogs).WithName("downloadHostLogs")
            .Audited()
            .RequireElevation();
        endpoints.MapGet(ContractInfo.BasePath + "/isos", ListIsosAsync).WithName("listIsos");
        endpoints.MapPut(ContractInfo.BasePath + "/isos/{name}", UploadIsoAsync).WithName("uploadIso")
            .Audited<string>(name => $"name={name}")
            .RequireElevation();
        endpoints.MapPatch(ContractInfo.BasePath + "/isos/{name}", RenameIsoAsync).WithName("renameIso")
            .Audited<RenameIsoRequest>(request => $"newName={request.NewName}")
            .RequireElevation();
        endpoints.MapDelete(ContractInfo.BasePath + "/isos/{name}", DeleteIsoAsync).WithName("deleteIso")
            .Audited<string>(name => $"name={name}")
            .RequireElevation();
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
        VmStorageLocation location,
        IOptions<LifecycleOptions> options,
        IsoLibrary library,
        CancellationToken cancellationToken)
    {
        var host = capacity.Read();
        return TypedResults.Ok(new HostResources(
            host.LogicalProcessorCount,
            host.TotalMemoryMb,
            host.AvailableMemoryMb,
            options.Value.HostMemoryReserveMb,
            await location.DisplayFolderAsync(cancellationToken),
            library.Folder));
    }

    private static Ok<HostRemoteDesktop> GetRemoteDesktop(HostRemoteDesktopService remoteDesktop) =>
        TypedResults.Ok(remoteDesktop.Get());

    private static Ok<HostRemoteDesktop> EnableRemoteDesktop(HostRemoteDesktopService remoteDesktop) =>
        TypedResults.Ok(remoteDesktop.Enable());

    /// <summary>The newest log files and a summary as a zip. Built in memory: logs are capped and compress well.</summary>
    private static FileContentHttpResult DownloadLogs(LogBundle bundle, TimeProvider time)
    {
        using var zip = new MemoryStream();
        bundle.Write(zip,
        [
            $"Host: {Environment.MachineName}",
            $"Host version: {HostVersion}",
            $"API version: {ContractInfo.ApiVersion}",
            $"Windows: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            $"System uptime: {TimeSpan.FromMilliseconds(Environment.TickCount64):d\\.hh\\:mm\\:ss}",
            $"Process started: {System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime():u}",
        ]);

        var fileName = $"hyperharbor-logs-{Environment.MachineName}-{time.GetUtcNow():yyyyMMdd-HHmmss}.zip";
        return TypedResults.File(zip.ToArray(), "application/zip", fileName);
    }

    private static async Task<Ok<IReadOnlyList<IsoImage>>> ListIsosAsync(IsoLibraryService isos, CancellationToken cancellationToken) =>
        TypedResults.Ok(await isos.ListAsync(cancellationToken));

    /// <summary>The body is the image itself (application/octet-stream), streamed to disk; there is no size limit.</summary>
    private static async Task<Created<IsoImage>> UploadIsoAsync(string name, HttpContext context, IsoLibraryService isos, CancellationToken cancellationToken)
    {
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = null;
        }

        var image = await isos.UploadAsync(name, context.Request.Body, context.Request.ContentLength, cancellationToken);
        return TypedResults.Created($"{ContractInfo.BasePath}/isos/{Uri.EscapeDataString(image.Name)}", image);
    }

    private static async Task<Ok<IsoImage>> RenameIsoAsync(string name, RenameIsoRequest request, IsoLibraryService isos, CancellationToken cancellationToken) =>
        TypedResults.Ok(await isos.RenameAsync(name, request.NewName, cancellationToken));

    private static async Task<NoContent> DeleteIsoAsync(string name, IsoLibraryService isos, CancellationToken cancellationToken)
    {
        await isos.DeleteAsync(name, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IReadOnlyList<VirtualSwitch>>> ListSwitchesAsync(IHyperVHost hyperV, CancellationToken cancellationToken) =>
        TypedResults.Ok(await hyperV.ListSwitchesAsync(cancellationToken));
}
