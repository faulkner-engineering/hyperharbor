using System.Text;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the Profiles tag of docs/api.yaml: each User's setup profiles, kept as YAML files on the host. Saving,
/// importing, and deleting need elevation, because a profile decides what is installed and removed in VMs.
/// </summary>
public static class SetupProfileEndpoints
{
    public const string ProfilesPath = ContractInfo.BasePath + "/setup-profiles";
    public const string YamlMediaType = "application/yaml";

    public static IEndpointRouteBuilder MapSetupProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var profiles = endpoints.MapGroup(ProfilesPath);
        profiles.MapGet("/", List).WithName("listSetupProfiles");
        profiles.MapPost("/", Create).WithName("createSetupProfile")
            .Audited<SetupProfile>(Describe)
            .RequireElevation();
        profiles.MapPost("/import", ImportAsync).WithName("importSetupProfile")
            .Audited()
            .RequireElevation();
        profiles.MapGet("/schema", Schema).WithName("getSetupProfileSchema");
        profiles.MapGet("/catalog", Catalog).WithName("getSetupProfileCatalog");
        profiles.MapGet("/{profileId}", Get).WithName("getSetupProfile");
        profiles.MapGet("/{profileId}/yaml", Export).WithName("exportSetupProfile");
        profiles.MapPut("/{profileId}", Update).WithName("updateSetupProfile")
            .Audited<SetupProfile>(Describe)
            .RequireElevation();
        profiles.MapDelete("/{profileId}", Delete).WithName("deleteSetupProfile")
            .Audited<string>(profileId => $"profileId={profileId}")
            .RequireElevation();

        endpoints.MapGroup(VmEndpoints.BasePath).MapPost("/{vmId:guid}/setup-profile", ApplyAsync).WithName("applyVmSetupProfile")
            .Audited<ApplySetupProfileRequest>(request => $"profileId={request.ProfileId}, restartIfNeeded={request.RestartIfNeeded}")
            .RequireElevation();
        return endpoints;
    }

    private static async Task<Accepted<VmJob>> ApplyAsync(
        Guid vmId,
        ApplySetupProfileRequest request,
        SetupProfileJobs setup,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await setup.StartAsync(vmId, context.User.UserId(), request, context.CompletionAuditor(), cancellationToken);
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static string Describe(SetupProfile profile) =>
        $"name={profile.Name}, install={profile.Install?.Count ?? 0}, tweaks={profile.Tweaks?.Count ?? 0}";

    private static Ok<IReadOnlyList<SetupProfileSummary>> List(SetupProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.List(context.User.UserId()));

    private static Ok<StoredSetupProfile> Get(string profileId, SetupProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.Get(context.User.UserId(), profileId));

    private static Created<StoredSetupProfile> Create(SetupProfile profile, SetupProfileStore store, HttpContext context)
    {
        var saved = store.Create(context.User.UserId(), profile);
        return TypedResults.Created($"{ProfilesPath}/{saved.Id}", saved);
    }

    private static Ok<StoredSetupProfile> Update(string profileId, SetupProfile profile, SetupProfileStore store, HttpContext context) =>
        TypedResults.Ok(store.Update(context.User.UserId(), profileId, profile));

    private static NoContent Delete(string profileId, SetupProfileStore store, HttpContext context)
    {
        store.Delete(context.User.UserId(), profileId);
        return TypedResults.NoContent();
    }

    private static ContentHttpResult Export(string profileId, SetupProfileStore store, HttpContext context) =>
        TypedResults.Text(store.GetYaml(context.User.UserId(), profileId), YamlMediaType, Encoding.UTF8);

    private static Ok<SetupProfileCatalog> Catalog(Catalogs catalogs) => TypedResults.Ok(new SetupProfileCatalog(
        catalogs.Tweaks.Select(tweak => new TweakCatalogItem(tweak.Id, tweak.Name, tweak.Category, tweak.Note)).ToList(),
        catalogs.Browsers.Select(browser => new BrowserCatalogItem(browser.Id, browser.Name, browser.WingetId, browser.Alias)).ToList(),
        catalogs.Policies.Select(policy => new BrowserPolicyItem(policy.Key, policy.Name, policy.Type, policy.AppliesTo, policy.Description, policy.Values)).ToList()));

    private static ContentHttpResult Schema() =>
        TypedResults.Text(Catalogs.ProfileSchema, "application/schema+json", Encoding.UTF8);

    /// <summary>The body is the YAML file itself.</summary>
    private static async Task<Created<StoredSetupProfile>> ImportAsync(HttpContext context, SetupProfileStore store, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength > SetupProfileStore.MaxFileBytes)
        {
            throw new LifecycleValidationException("The file is too large.", [new ValidationIssue("yaml", $"A setup profile file is at most {SetupProfileStore.MaxFileBytes / 1024} KB.")]);
        }

        using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
        var buffer = new char[SetupProfileStore.MaxFileBytes + 1];
        var length = await reader.ReadBlockAsync(buffer, cancellationToken);
        var saved = store.Import(context.User.UserId(), new string(buffer, 0, length));
        return TypedResults.Created($"{ProfilesPath}/{saved.Id}", saved);
    }
}
