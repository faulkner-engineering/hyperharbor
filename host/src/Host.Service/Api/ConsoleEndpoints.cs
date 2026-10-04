using System.Globalization;
using System.Net.Sockets;
using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Api;

/// <summary>
/// Maps the console operations of docs/api.yaml. A console session opens Remote Desktop to the host's
/// Virtual Machine Connection service through tunnels over this API, so the console works from any
/// paired device without opening another port.
/// </summary>
public static class ConsoleEndpoints
{
    private static readonly TimeSpan UpstreamConnectTimeout = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapConsoleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var vms = endpoints.MapGroup(VmEndpoints.BasePath);

        vms.MapPost("/{vmId:guid}/console", OpenAsync).WithName("openVmConsole").Audited();

        // The tunnel's completion entry is written when the tunnel closes, with its duration and byte counts.
        vms.MapPost("/{vmId:guid}/console/tunnel", TunnelAsync).WithName("openVmConsoleTunnel").Audited();

        return endpoints;
    }

    private static async Task<Ok<ConsoleSession>> OpenAsync(
        Guid vmId,
        ConsoleService console,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var session = await console.OpenAsync(
            vmId,
            context.User.UserId(),
            context.User.DeviceId(),
            context.User.Identity?.Name ?? "unknown device",
            cancellationToken);

        // The body carries a password and a ticket; no cache may keep them.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        return TypedResults.Ok(session);
    }

    internal static async Task<IResult> TunnelAsync(
        Guid vmId,
        HttpContext context,
        ConsoleTicketStore tickets,
        IOptions<ConsoleOptions> options,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        tickets.Validate(context.Request.Headers[ContractInfo.ConsoleTicketHeader], context.User.DeviceId(), context.User.UserId(), vmId);

        var upgrade = context.Features.Get<IHttpUpgradeFeature>();
        if (upgrade is not { IsUpgradableRequest: true }
            || !string.Equals(context.Request.Headers.Upgrade, ContractInfo.ConsoleUpgradeProtocol, StringComparison.OrdinalIgnoreCase))
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status426UpgradeRequired,
                title: "Upgrade required",
                detail: $"Send this request over HTTP/1.1 with Connection: Upgrade and Upgrade: {ContractInfo.ConsoleUpgradeProtocol}.");
        }

        // Connect before upgrading, so an unreachable console service is still a normal error response.
        using var upstream = new TcpClient();
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            connectTimeout.CancelAfter(UpstreamConnectTimeout);
            try
            {
                await upstream.ConnectAsync(options.Value.EndpointAddress, connectTimeout.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
            {
                throw new ConsoleUnavailableException("The host's Virtual Machine Connection service did not answer. Check that Hyper-V is running.", ex);
            }
        }

        upstream.NoDelay = true;
        context.Response.Headers.Upgrade = ContractInfo.ConsoleUpgradeProtocol;
        await using var client = await upgrade.UpgradeAsync();
        await using var vm = upstream.GetStream();

        var logger = loggers.CreateLogger(typeof(ConsoleEndpoints).FullName!);
        logger.LogInformation("Console tunnel opened for VM {VmId}.", vmId);
        var result = await ConsoleTunnel.PumpAsync(client, vm, time, context.RequestAborted);
        logger.LogInformation(
            "Console tunnel closed for VM {VmId} after {Seconds:F0} s ({ToVm} bytes to the VM, {FromVm} bytes from it).",
            vmId,
            result.Duration.TotalSeconds,
            result.BytesToVm,
            result.BytesFromVm);

        if (context.Audit() is { } audit)
        {
            audit.Detail = string.Create(
                CultureInfo.InvariantCulture,
                $"durationSeconds={result.Duration.TotalSeconds:F0}, bytesToVm={result.BytesToVm}, bytesFromVm={result.BytesFromVm}");
        }

        return Results.Empty;
    }
}
