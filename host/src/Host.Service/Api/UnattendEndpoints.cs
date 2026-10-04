using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Unattend;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the unattended install operations of docs/api.yaml: profiles, and what an ISO installs. Changing
/// a profile needs elevation, because a profile decides what is installed in future VMs (for example
/// which SSH keys may sign in).
/// </summary>
public static class UnattendEndpoints
{
    public const string ProfilesPath = ContractInfo.BasePath + "/unattend-profiles";

    public static IEndpointRouteBuilder MapUnattendEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profiles = endpoints.MapGroup(ProfilesPath);
        profiles.MapGet("/", List).WithName("listUnattendProfiles");
        profiles.MapPost("/", Create).WithName("createUnattendProfile")
            .Audited<UnattendProfileRequest>(Describe)
            .RequireElevation();
        profiles.MapPut("/{profileId}", Update).WithName("updateUnattendProfile")
            .Audited<UnattendProfileRequest>(Describe)
            .RequireElevation();
        profiles.MapDelete("/{profileId}", Delete).WithName("deleteUnattendProfile")
            .Audited<string>(profileId => $"profileId={profileId}")
            .RequireElevation();

        endpoints.MapGet(ContractInfo.BasePath + "/isos/{name}/inspection", InspectAsync).WithName("inspectIso");
        return endpoints;
    }

    /// <summary>Name and OS only: SSH keys and package lists stay out of the audit log.</summary>
    private static string Describe(UnattendProfileRequest request) => $"name={request.Name}, os={request.Os}";

    private static Ok<IReadOnlyList<UnattendProfile>> List(UnattendProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.List(context.User.UserId()));

    private static Created<UnattendProfile> Create(UnattendProfileRequest request, UnattendProfileStore store, HttpContext context)
    {
        var profile = store.Create(context.User.UserId(), request);
        return TypedResults.Created($"{ProfilesPath}/{profile.Id}", profile);
    }

    private static Ok<UnattendProfile> Update(string profileId, UnattendProfileRequest request, UnattendProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.Update(context.User.UserId(), profileId, request));

    private static NoContent Delete(string profileId, UnattendProfileStore store, HttpContext context)
    {
        store.Delete(context.User.UserId(), profileId);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<IsoInspection>> InspectAsync(string name, IsoLibrary isos, UnattendedSetup unattended, CancellationToken cancellationToken)
    {
        var path = isos.PathOf(name);
        return TypedResults.Ok(await Task.Run(() => unattended.Inspect(path), cancellationToken));
    }
}
