using System.Globalization;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.HttpResults;

namespace HyperHarbor.Host.Service.Api;

/// <summary>Maps the Performance mode operations of docs/api.yaml and GET /host/gpu.</summary>
public static class PerformanceEndpoints
{
    public static IEndpointRouteBuilder MapPerformanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(VmEndpoints.BasePath);
        vms.MapGet("/{vmId:guid}/performance", GetAsync).WithName("getVmPerformance");
        vms.MapPut("/{vmId:guid}/performance", ApplyAsync).WithName("applyVmPerformance")
            .Audited<PerformanceSettings>(Describe)
            .RequireElevation();
        vms.MapDelete("/{vmId:guid}/performance", RemoveAsync).WithName("removeVmPerformance")
            .Audited()
            .RequireElevation();
        vms.MapPost("/{vmId:guid}/performance/guest-setup", GuestSetupAsync).WithName("setUpVmPerformanceGuest")
            .Audited<GuestSetupRequest>(request => $"driversOnly={request.DriversOnly}")
            .RequireElevation();

        endpoints.MapGet(ContractInfo.BasePath + "/host/gpu", GetHostGpuAsync).WithName("getHostGpu");
        return endpoints;
    }

    private static string Describe(PerformanceSettings request) => string.Create(
        CultureInfo.InvariantCulture,
        $"processors={request.ProcessorCount}, memoryMb={request.MemoryMb}, " +
        $"gpu={request.Gpu?.VramPercent ?? 50}/{request.Gpu?.EncodePercent ?? 50}/{request.Gpu?.DecodePercent ?? 50}/{request.Gpu?.ComputePercent ?? 50}%, " +
        $"mmioMb={request.Mmio?.LowGapMb ?? 1024}/{request.Mmio?.HighGapMb ?? 32768}, moveStorage={request.MoveStorageTo is not null}, " +
        $"hardwareEncoding={request.Rdp?.HardwareEncoding ?? false}");

    private static async Task<Ok<VmPerformance>> GetAsync(Guid vmId, VmPerformanceService performance, CancellationToken cancellationToken) =>
        TypedResults.Ok(await performance.GetAsync(vmId, cancellationToken));

    private static async Task<Accepted<VmJob>> ApplyAsync(
        Guid vmId,
        PerformanceSettings request,
        VmPerformanceService performance,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await performance.ApplyAsync(vmId, context.User.UserId(), request, context.CompletionAuditor(), cancellationToken);
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static async Task<Accepted<VmJob>> GuestSetupAsync(
        Guid vmId,
        GuestSetupRequest request,
        VmPerformanceService performance,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var job = await performance.StartGuestSetupAsync(vmId, context.User.UserId(), request.DriversOnly, context.CompletionAuditor(), cancellationToken);
        context.Audit()!.JobId = job.Id;
        return TypedResults.Accepted(JobEndpoints.Location(job.Id), job.ToContract());
    }

    private static async Task<NoContent> RemoveAsync(Guid vmId, VmPerformanceService performance, CancellationToken cancellationToken)
    {
        await performance.RemoveAsync(vmId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<HostGpu>> GetHostGpuAsync(IHostGpuReader gpus, CancellationToken cancellationToken)
    {
        var devices = await gpus.ReadAsync(cancellationToken);
        return TypedResults.Ok(new HostGpu(devices.Select(gpu => gpu.ToContract()).ToList(), []));
    }
}
