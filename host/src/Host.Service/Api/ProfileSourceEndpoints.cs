using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps what setup profiles are built from (Profiles tag of docs/api.yaml): a VM's provisioned Appx packages
/// and the clean baselines they are compared with. Reading a VM only reads; recording a baseline is audited.
/// </summary>
public static class ProfileSourceEndpoints
{
    public static IEndpointRouteBuilder MapProfileSourceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(VmEndpoints.BasePath);
        vms.MapGet("/{vmId:guid}/appx", ListAppxAsync).WithName("listVmAppx");
        vms.MapPost("/{vmId:guid}/appx-baseline", RecordBaselineAsync).WithName("recordAppxBaseline").Audited();
        vms.MapPost("/{vmId:guid}/profile-capture", CaptureAsync).WithName("captureSetupProfile").Audited();

        endpoints.MapGet(ContractInfo.BasePath + "/packages/search", SearchPackagesAsync).WithName("searchPackages");
        endpoints.MapGet(ContractInfo.BasePath + "/packages/catalog", PackageCatalog).WithName("listPackageCatalog");

        endpoints.MapGet(ContractInfo.BasePath + "/extensions/catalog", ExtensionCatalog).WithName("listExtensionCatalog");
        endpoints.MapGet(ContractInfo.BasePath + "/extensions/resolve", ResolveExtensionAsync).WithName("resolveExtension");
        return endpoints;
    }

    private static Ok<IReadOnlyList<ExtensionCatalogItem>> ExtensionCatalog(Catalogs catalogs) =>
        TypedResults.Ok<IReadOnlyList<ExtensionCatalogItem>>(catalogs.Extensions
            .Select(entry => new ExtensionCatalogItem(entry.ProfileId, entry.Name, entry.Category, entry.Description, entry.EdgeId is null ? null : ExtensionRef.EdgePrefix + entry.EdgeId))
            .ToList());

    private static async Task<Ok<ResolvedExtension>> ResolveExtensionAsync(string? input, IExtensionResolver resolver, CancellationToken cancellationToken)
    {
        try
        {
            return TypedResults.Ok(await resolver.ResolveAsync(input ?? "", cancellationToken));
        }
        catch (ExtensionInputException ex)
        {
            throw new LifecycleValidationException(ex.Message, [new ValidationIssue("input", ex.Message)]);
        }
    }

    private static async Task<Ok<IReadOnlyList<PackageSearchResult>>> SearchPackagesAsync(string? q, int? count, IPackageSearch search, CancellationToken cancellationToken)
    {
        var query = q?.Trim() ?? "";
        if (query.Length is 0 or > PwshPackageSearch.MaxQuery || query.Any(char.IsControl))
        {
            throw new LifecycleValidationException("Enter something to search for.", [new ValidationIssue("q", $"Search for 1 to {PwshPackageSearch.MaxQuery} characters.")]);
        }

        return TypedResults.Ok(await search.SearchAsync(query, Math.Clamp(count ?? 20, 1, PwshPackageSearch.MaxCount), cancellationToken));
    }

    private static Ok<IReadOnlyList<PackageCatalogItem>> PackageCatalog(Catalogs catalogs) =>
        TypedResults.Ok<IReadOnlyList<PackageCatalogItem>>(catalogs.Packages
            .Select(entry => new PackageCatalogItem(entry.Alias, entry.Id, entry.Name, entry.Category, entry.Popular))
            .ToList());

    /// <summary>Only a running VM can be read today; golden images will be another source.</summary>
    private static async Task<Ok<VmAppxInventory>> ListAppxAsync(Guid vmId, string? source, AppxInventoryService appx, CancellationToken cancellationToken)
    {
        if (source is not (null or "vm"))
        {
            throw new LifecycleValidationException("Unknown source.", [new ValidationIssue("source", "The only source is vm, a running Windows VM.")]);
        }

        return TypedResults.Ok(await appx.ListAsync(vmId, cancellationToken));
    }

    /// <summary>Reads only; the draft is returned, and nothing is saved until the client saves a profile.</summary>
    private static async Task<Ok<ProfileDraft>> CaptureAsync(Guid vmId, ProfileCaptureService capture, HttpContext context, CancellationToken cancellationToken) =>
        TypedResults.Ok(await capture.CaptureAsync(vmId, context.User.UserId(), cancellationToken));

    private static async Task<Ok<AppxBaselineInfo>> RecordBaselineAsync(Guid vmId, AppxInventoryService appx, CancellationToken cancellationToken) =>
        TypedResults.Ok((await appx.RecordBaselineAsync(vmId, AppxBaselineSource.Manual, onlyIfMissing: false, cancellationToken))!);
}
