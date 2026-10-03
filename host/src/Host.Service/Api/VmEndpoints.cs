using System.Security.Claims;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Lifecycle;
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

        vms.MapPost("/", CreateAsync).WithName("createVm")
            .Audited<CreateVmRequest>(request =>
                $"name={request.Name}, iso={request.IsoName}, diskSizeGb={request.DiskSizeGb}, processors={request.ProcessorCount}, " +
                $"startupMemoryMb={request.StartupMemoryMb}, maximumMemoryMb={request.MaximumMemoryMb}, dynamicMemory={request.DynamicMemory}, " +
                $"switchId={request.SwitchId ?? "default"}, tpm={request.EnableTpm}")
            .RequireElevation();

        vms.MapGet("/{vmId:guid}/compute", GetComputeAsync).WithName("getVmCompute");
        vms.MapPatch("/{vmId:guid}/compute", UpdateComputeAsync).WithName("updateVmCompute")
            .Audited<UpdateVmComputeRequest>(request =>
                $"processors={request.ProcessorCount?.ToString() ?? "-"}, startupMemoryMb={request.StartupMemoryMb?.ToString() ?? "-"}, " +
                $"maximumMemoryMb={request.MaximumMemoryMb?.ToString() ?? "-"}, dynamicMemory={request.DynamicMemory?.ToString() ?? "-"}, " +
                $"nested={request.NestedVirtualization?.ToString() ?? "-"}, macSpoofing={request.MacAddressSpoofing?.ToString() ?? "-"}, " +
                $"shutDownToApply={request.ShutDownToApply}")
            .RequireElevation();

        vms.MapGet("/{vmId:guid}/delete-preview", GetDeletePreviewAsync).WithName("getVmDeletePreview");
        vms.MapPost("/{vmId:guid}/delete", DeleteAsync).WithName("deleteVm")
            .Audited<VmDeleteRequest>(request => $"deleteDisks={request.DeleteDisks}, deleteCheckpoints={request.DeleteCheckpoints}")
            .RequireElevation();

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

    private static async Task<Accepted<VmJob>> CreateAsync(
        CreateVmRequest request,
        VmCreationService creation,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var audit = context.Audit()!;
        audit.VmName = request.Name?.Trim();
        var job = await creation.StartAsync(context.User.UserId(), request, context.CompletionAuditor(), cancellationToken);
        audit.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static async Task<Ok<VmComputeSettings>> GetComputeAsync(Guid vmId, VmComputeService compute, CancellationToken cancellationToken) =>
        TypedResults.Ok(await compute.GetAsync(vmId, cancellationToken));

    /// <summary>200 with the new settings when applied at once; 202 with a job when the VM is shut down to apply them.</summary>
    private static async Task<IResult> UpdateComputeAsync(
        Guid vmId,
        UpdateVmComputeRequest request,
        VmComputeService compute,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var update = await compute.UpdateAsync(vmId, context.User.UserId(), request, JobEndpoints.ToContract, context.CompletionAuditor(), cancellationToken);
        if (update.Job is { } job)
        {
            context.Audit()!.JobId = job.Id;
            return TypedResults.Accepted(JobEndpoints.Location(job.Id), update);
        }

        return TypedResults.Ok(update);
    }

    private static async Task<Ok<VmDeletePreview>> GetDeletePreviewAsync(Guid vmId, VmDeletionService deletion, CancellationToken cancellationToken) =>
        TypedResults.Ok(await deletion.PreviewAsync(vmId, cancellationToken));

    private static async Task<Accepted<VmJob>> DeleteAsync(
        Guid vmId,
        VmDeleteRequest request,
        VmDeletionService deletion,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await deletion.StartAsync(vmId, context.User.UserId(), request, context.CompletionAuditor(), cancellationToken);
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    /// <summary>Adds the per-User fields; the inventory itself is shared by all Users.</summary>
    private static Vm ForUser(Vm vm, Guid userId, ProvisioningStore provisioning) => vm with
    {
        Provisioned = provisioning.Find(vm.Id, userId) is not null,
        RemoteDesktop = vm.RemoteDesktop ?? new VmRemoteDesktop(vm.IpAddresses.FirstOrDefault(), vm.RdpAvailable),
    };
}
