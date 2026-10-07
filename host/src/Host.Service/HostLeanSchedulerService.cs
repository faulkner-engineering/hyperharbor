using HyperHarbor.Host.Core.HostProfiles;

namespace HyperHarbor.Host.Service;

/// <summary>
/// Checks every hour whether the monthly re-apply of the Lean host profile is due, and runs it. The first check waits
/// ten minutes after the service starts, so it never competes with the PC starting up. The re-apply runs only after a
/// person applied the shipped profile once; see <see cref="HostLeanService.TickAsync"/>.
/// </summary>
internal sealed class HostLeanSchedulerService(HostLeanService lean, ILogger<HostLeanSchedulerService> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await lean.TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A failing re-apply must never take the host down.
                    logger.LogError(ex, "The monthly Lean host re-apply failed unexpectedly.");
                }

                await Task.Delay(CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The service is stopping.
        }
    }
}
