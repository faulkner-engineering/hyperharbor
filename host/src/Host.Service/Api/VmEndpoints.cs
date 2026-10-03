using System.Security.Claims;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Service.Security;
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
        vms.MapPost("/{vmId:guid}/actions", PerformVmActionAsync).WithName("performVmAction")
            .Audited<VmActionRequest>(request => $"action={request.Action}")
            .RequireElevation(arguments => arguments.OfType<VmActionRequest>().Any(request => request.Action == VmAction.TurnOff));

        // The admin password is deliberately left out of the summary.
        vms.MapPost("/{vmId:guid}/provision", ProvisionAsync).WithName("provisionVm")
            .Audited<ProvisionVmRequest>(request =>
                $"adminUserName={request.AdminUserName}, enableRemoteDesktop={request.EnableRemoteDesktop}, " +
                $"installDesktop={request.InstallDesktop}, trustNewHostKey={request.TrustNewHostKey}");
        vms.MapPost("/{vmId:guid}/connect", ConnectAsync).WithName("connectVm").Audited();

        return endpoints;
    }

    private static async Task<Ok<IReadOnlyList<Vm>>> ListVmsAsync(
        IVmInventory inventory,
        ProvisioningStore provisioning,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var userId = user.UserId();
        var vms = await inventory.ListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<Vm>>(vms.Select(vm => ForUser(vm, userId, provisioning)).ToList());
    }

    private static async Task<Results<Ok<Vm>, ProblemHttpResult>> GetVmAsync(
        Guid vmId,
        IVmInventory inventory,
        ProvisioningStore provisioning,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(vmId, cancellationToken);
        return vm is null
            ? TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: "Virtual machine not found", detail: $"Virtual machine {vmId} was not found.")
            : TypedResults.Ok(ForUser(vm, user.UserId(), provisioning));
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

    private static async Task<Ok<VmProvisioning>> ProvisionAsync(
        Guid vmId,
        ProvisionVmRequest request,
        ProvisioningService provisioning,
        ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        return TypedResults.Ok(await provisioning.ProvisionAsync(vmId, user.UserId(), request, cancellationToken));
    }

    private static async Task<Ok<VmConnection>> ConnectAsync(
        Guid vmId,
        ConnectService connect,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var connection = await connect.ConnectAsync(vmId, context.User.UserId(), context.User.Identity?.Name ?? "unknown device", cancellationToken);

        // The body carries a password; no cache may keep it.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(connection);
    }

    /// <summary>Adds the per-User fields; the inventory itself is shared by all Users.</summary>
    private static Vm ForUser(Vm vm, Guid userId, ProvisioningStore provisioning) => vm with
    {
        Provisioned = provisioning.Find(vm.Id, userId) is not null,
        RemoteDesktop = vm.RemoteDesktop ?? new VmRemoteDesktop(vm.IpAddresses.FirstOrDefault(), vm.RdpAvailable),
    };
}
