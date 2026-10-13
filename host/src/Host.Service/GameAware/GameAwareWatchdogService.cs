using HyperHarbor.Host.Core.GameAware;

namespace HyperHarbor.Host.Service.GameAware;

/// <summary>
/// Runs the game-aware watchdog: once at start (VMs an earlier run left paused are resumed) and then every ten seconds, so
/// VMs come back when the tray stops reporting, when no game was reported for a minute, or when the mode is turned off.
/// </summary>
internal sealed class GameAwareWatchdogService(GameAwareController controller, TimeProvider time, ILogger<GameAwareWatchdogService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await controller.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The game-aware watchdog failed; it tries again.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
