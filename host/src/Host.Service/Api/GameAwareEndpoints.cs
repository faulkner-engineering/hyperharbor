using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;
using Microsoft.AspNetCore.Http.HttpResults;
using WireSettings = HyperHarbor.Shared.Contracts.Hosts.GameAwareSettings;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the game-aware routes of docs/api.yaml.</summary>
public static class GameAwareEndpoints
{
    public static IEndpointRouteBuilder MapGameAwareEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ContractInfo.BasePath + "/host/game-aware");
        group.MapGet(string.Empty, GetAsync).WithName("getGameAware");
        group.MapPut(string.Empty, Set).WithName("setGameAware")
            .Audited<WireSettings>(request =>
                $"enabled={request.Enabled}, default={request.DefaultAction}, vmPolicies={request.VmPolicies.Count}, allowlist={request.Allowlist.Count}, ignoreList={request.IgnoreList.Count}")
            .RequireElevation();
        group.MapPost("/not-a-game", NotAGameAsync).WithName("notAGame").Audited();
        return endpoints;
    }

    private static async Task<Ok<GameAwareInfo>> GetAsync(GameAwareService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.GetAsync(cancellationToken));

    private static Ok<WireSettings> Set(WireSettings request, GameAwareService service) =>
        TypedResults.Ok(service.Apply(request));

    private static async Task<Ok<NotAGameResult>> NotAGameAsync(GameAwareService service, CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.NotAGameAsync(cancellationToken));
}
