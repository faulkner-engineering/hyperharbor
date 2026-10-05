using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>Runs <see cref="UpdateCoordinator.TickAsync"/> every minute, and at once when the owner asks for a check or an install.</summary>
internal sealed class UpdateService(UpdateCoordinator coordinator, ILogger<UpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.WhenAny(Task.Delay(TickInterval, stoppingToken), coordinator.WaitForRequestAsync(stoppingToken));
                stoppingToken.ThrowIfCancellationRequested();
                await coordinator.TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Updates must never take the host down.
                logger.LogError(ex, "The update check failed unexpectedly.");
            }
        }
    }
}

/// <summary>
/// Counts state-changing requests in <see cref="HostActivity"/>, and refuses them with 503 "updating" while an
/// update is being installed. Reads pass, so clients keep showing the host until the service restarts.
/// </summary>
internal static class UpdateGateMiddleware
{
    public const int RetryAfterSeconds = 120;

    public static IApplicationBuilder UseUpdateGate(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method))
        {
            await next(context);
            return;
        }

        using var request = context.RequestServices.GetRequiredService<HostActivity>().TryBegin();
        if (request is null)
        {
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await Results.Problem(
                detail: "The host is installing an update and restarts shortly. Try again in a few minutes.",
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The host is updating",
                extensions: new Dictionary<string, object?> { ["code"] = ContractInfo.ProblemCodes.Updating }).ExecuteAsync(context);
            return;
        }

        await next(context);
    });
}
