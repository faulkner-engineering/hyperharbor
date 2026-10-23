using System.Text;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the machine profile routes of the Profiles tag in docs/api.yaml (API 1.27.0): each User's machine profiles,
/// kept as YAML files on the host. Saving, importing, and deleting need elevation, as for setup profiles.
/// </summary>
public static class MachineProfileEndpoints
{
    public const string ProfilesPath = ContractInfo.BasePath + "/machine-profiles";

    public static IEndpointRouteBuilder MapMachineProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profiles = endpoints.MapGroup(ProfilesPath);
        profiles.MapGet("/", List).WithName("listMachineProfiles");
        profiles.MapPost("/", Create).WithName("createMachineProfile")
            .Audited<MachineProfile>(Describe)
            .RequireElevation();
        profiles.MapPost("/import", ImportAsync).WithName("importMachineProfile")
            .Audited()
            .RequireElevation();
        profiles.MapGet("/schema", Schema).WithName("getMachineProfileSchema");
        profiles.MapGet("/{profileId}", Get).WithName("getMachineProfile");
        profiles.MapGet("/{profileId}/yaml", Export).WithName("exportMachineProfile");
        profiles.MapPut("/{profileId}", Update).WithName("updateMachineProfile")
            .Audited<MachineProfile>(Describe)
            .RequireElevation();
        profiles.MapDelete("/{profileId}", Delete).WithName("deleteMachineProfile")
            .Audited<string>(profileId => $"profileId={profileId}")
            .RequireElevation();
        profiles.MapPost("/{profileId}/vms", CreateVmAsync).WithName("createVmFromMachineProfile")
            .Audited<CreateVmFromProfileRequest>(request => $"name={request.Name}, iso={request.IsoName}, edition={request.WindowsEdition ?? "profile"}")
            .RequireElevation();
        return endpoints;
    }

    private static async Task<Accepted<VmJob>> CreateVmAsync(
        string profileId,
        CreateVmFromProfileRequest request,
        MachineProfileVmCreation creation,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var audit = context.Audit()!;
        audit.VmName = request.Name?.Trim();
        var job = await creation.StartAsync(context.User.UserId(), profileId, request, context.CompletionAuditor(), cancellationToken);
        audit.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static string Describe(MachineProfile profile) =>
        $"name={profile.Name}, os={profile.Os}, gpu={profile.Gpu?.Enabled == true}, backup={profile.Backup?.Enabled == true}, install={profile.Install?.Count ?? 0}";

    private static Ok<IReadOnlyList<MachineProfileSummary>> List(MachineProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.List(context.User.UserId()));

    private static Ok<StoredMachineProfile> Get(string profileId, MachineProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.Get(context.User.UserId(), profileId));

    private static Created<StoredMachineProfile> Create(MachineProfile profile, MachineProfileStore store, HttpContext context)
    {
        var saved = store.Create(context.User.UserId(), profile);
        return TypedResults.Created($"{ProfilesPath}/{saved.Id}", saved);
    }

    private static Ok<StoredMachineProfile> Update(string profileId, MachineProfile profile, MachineProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.Update(context.User.UserId(), profileId, profile));

    private static NoContent Delete(string profileId, MachineProfileStore store, HttpContext context)
    {
        store.Delete(context.User.UserId(), profileId);
        return TypedResults.NoContent();
    }

    private static ContentHttpResult Export(string profileId, MachineProfileStore store, HttpContext context) =>
        TypedResults.Text(store.GetYaml(context.User.UserId(), profileId), SetupProfileEndpoints.YamlMediaType, Encoding.UTF8);

    private static ContentHttpResult Schema() =>
        TypedResults.Text(Catalogs.MachineProfileSchema, "application/schema+json", Encoding.UTF8);

    /// <summary>The body is the YAML file itself.</summary>
    private static async Task<Created<StoredMachineProfile>> ImportAsync(HttpContext context, MachineProfileStore store, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > MachineProfileStore.MaxFileBytes)
        {
            throw new LifecycleValidationException("The file is too large.", [new ValidationIssue("yaml", $"A machine profile file is at most {MachineProfileStore.MaxFileBytes / 1024} KB.")]);
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var buffer = new char[MachineProfileStore.MaxFileBytes + 1];
        var length = await reader.ReadBlockAsync(buffer, cancellationToken);
        var saved = store.Import(context.User.UserId(), new string(buffer, 0, length));
        return TypedResults.Created($"{ProfilesPath}/{saved.Id}", saved);
    }
}
