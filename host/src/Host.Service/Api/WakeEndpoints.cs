using System.Security.Claims;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Wake;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Wake;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Wake tag of docs/api.yaml.</summary>
public static class WakeEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/wake";

    public static IEndpointRouteBuilder MapWakeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var wake = endpoints.MapGroup(BasePath);

        wake.MapGet("/info", GetInfoAsync).WithName("getWakeInfo");
        wake.MapGet("/readiness", GetReadinessAsync).WithName("getWakeReadiness");
        wake.MapPost("/readiness/fix", FixAsync).WithName("fixWakeReadiness")
            .Audited<WakeFixRequest>(request => $"checkIds={string.Join(",", request.CheckIds)}")
            .RequireElevation();
        wake.MapPost("/test", StartTest).WithName("startWakeTest")
            .Audited<WakeTestRequest>(request => $"delaySeconds={request.DelaySeconds}");

        return endpoints;
    }

    private static async Task<Ok<WakeInfo>> GetInfoAsync(IWakeEnvironmentReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(WakeInfoBuilder.Build(await reader.ReadAsync(cancellationToken)));

    private static async Task<Ok<WakeReadiness>> GetReadinessAsync(IWakeEnvironmentReader reader, CancellationToken cancellationToken) =>
        TypedResults.Ok(WakeReadinessEvaluator.Evaluate(await reader.ReadAsync(cancellationToken)));

    private static async Task<Results<Ok<WakeReadiness>, Accepted>> FixAsync(
        WakeFixRequest request,
        WakeFixCoordinator coordinator,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var (outcome, readiness) = await coordinator.RequestAsync(request.CheckIds, DeviceName(user), cancellationToken);
        return outcome == WakeFixOutcome.Applied
            ? TypedResults.Ok(readiness!)
            : TypedResults.Accepted($"{BasePath}/readiness");
    }

    private static Accepted<WakeTestScheduled> StartTest(WakeTestRequest request, WakeTestScheduler scheduler, ClaimsPrincipal user)
    {
        var sleepAt = scheduler.Schedule(TimeSpan.FromSeconds(request.DelaySeconds), DeviceName(user));
        return TypedResults.Accepted((string?)null, new WakeTestScheduled(sleepAt));
    }

    private static string DeviceName(ClaimsPrincipal user) => user.Identity?.Name ?? "unknown device";
}
