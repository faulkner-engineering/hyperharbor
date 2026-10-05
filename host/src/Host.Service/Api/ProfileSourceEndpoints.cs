using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Service.Audit;
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

        endpoints.MapGet(ContractInfo.BasePath + "/packages/search", SearchPackagesAsync).WithName("searchPackages");
        endpoints.MapGet(ContractInfo.BasePath + "/packages/catalog", PackageCatalog).WithName("listPackageCatalog");
        return endpoints;
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

    private static async Task<Ok<AppxBaselineInfo>> RecordBaselineAsync(Guid vmId, AppxInventoryService appx, CancellationToken cancellationToken) =>
        TypedResults.Ok((await appx.RecordBaselineAsync(vmId, AppxBaselineSource.Manual, onlyIfMissing: false, cancellationToken))!);
}
