using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the VMs tag of docs/api.yaml.
/// </summary>
public static class VmEndpoints
{
    public const string BasePath = ContractInfo.BasePath + "/vms";

    public static IEndpointRouteBuilder MapVmEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(BasePath);

        vms.MapGet("/", ListVmsAsync).WithName("listVms");
        vms.MapGet("/{vmId:guid}", GetVmAsync).WithName("getVm");
        vms.MapPost("/{vmId:guid}/actions", PerformVmActionAsync).WithName("performVmAction");

        return endpoints;
    }

    private static async Task<Ok<IReadOnlyList<Vm>>> ListVmsAsync(IVmInventory inventory, CancellationToken cancellationToken)
    {
        return TypedResults.Ok(await inventory.ListAsync(cancellationToken));
    }

    private static async Task<Results<Ok<Vm>, ProblemHttpResult>> GetVmAsync(
        Guid vmId,
        IVmInventory inventory,
        CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken);
        return vm is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Virtual machine not found", detail: $"Virtual machine {vmId} was not found.")
            : TypedResults.Ok(vm);
    }

    private static async Task<Accepted<VmActionResult>> PerformVmActionAsync(
        Guid vmId,
        VmActionRequest request,
        IVmPowerService powerService,
        CancellationToken cancellationToken)
    {
        var result = await powerService.PerformAsync(vmId, request.Action, cancellationToken);
        return TypedResults.Accepted($"{BasePath}/{vmId}", result);
    }
}
