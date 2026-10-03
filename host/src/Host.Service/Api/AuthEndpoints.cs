using System.Security.Claims;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Auth;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Auth tag of docs/api.yaml: elevation with the host's admin passphrase.</summary>
public static class AuthEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/auth";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup(BasePath);

        auth.MapGet("/elevation", GetElevation).WithName("getElevation");

        // Audited without a summary: the only parameter is the passphrase.
        auth.MapPost("/elevation", ElevateAsync).WithName("elevate").Audited();
        auth.MapDelete("/elevation", DropElevation).WithName("dropElevation").Audited();

        return endpoints;
    }

    private static Ok<ElevationStatus> GetElevation(ElevationService elevation, ClaimsPrincipal user) =>
        TypedResults.Ok(elevation.Status(user.DeviceId()));

    private static async Task<Ok<ElevationGrant>> ElevateAsync(
        ElevateRequest request,
        ElevationService elevation,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var grant = await elevation.ElevateAsync(context.User.DeviceId(), context.User.UserId(), request.Passphrase, cancellationToken);

        // The body carries a token; no cache may keep it.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(grant);
    }

    private static NoContent DropElevation(ElevationService elevation, ClaimsPrincipal user)
    {
        elevation.Drop(user.DeviceId());
        return TypedResults.NoContent();
    }
}
