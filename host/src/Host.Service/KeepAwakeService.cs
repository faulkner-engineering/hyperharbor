using HyperHarbor.Host.Core.Power;

namespace HyperHarbor.Host.Service;

/// <summary>
/// Runs <see cref="KeepAwakeController.Tick"/> every few seconds, and at once when a paired device starts using
/// the host again, so a host woken by Wake-on-LAN does not go back to sleep while a device is working with it.
/// </summary>
internal sealed class KeepAwakeService(KeepAwakeController controller, RemoteUseTracker tracker, KeepAwakeOptions options, ILogger<KeepAwakeService> logger)
    : BackgroundService
{
    private readonly SemaphoreSlim _resumed = new(0);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        tracker.Resumed += OnResumed;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        tracker.Resumed -= OnResumed;
        await base.StopAsync(cancellationToken);
        controller.Release();
    }

    public override void Dispose()
    {
        _resumed.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Keeping the host awake during remote use is turned off (KeepAwake:Enabled).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(5, options.PollSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                controller.Tick();
            }
            catch (Exception ex)
            {
                // Keeping awake must never take the host down.
                logger.LogError(ex, "The keep-awake check failed unexpectedly.");
            }

            await Task.WhenAny(Task.Delay(interval, stoppingToken), _resumed.WaitAsync(stoppingToken));
        }
    }

    private void OnResumed() => _resumed.Release();
}

/// <summary>Records each request from a paired device in <see cref="RemoteUseTracker"/>.</summary>
internal static class RemoteUseMiddleware
{
    /// <summary>Runs after authorization, so only requests from paired devices count; pairing itself does not.</summary>
    public static IApplicationBuilder UseRemoteUseTracking(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            context.RequestServices.GetRequiredService<RemoteUseTracker>().MarkUsed();
        }

        await next(context);
    });
}
