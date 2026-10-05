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
        return endpoints;
    }

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
