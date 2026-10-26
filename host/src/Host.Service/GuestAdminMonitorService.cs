using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Service;

/// <summary>
/// Runs <see cref="GuestAdminService.SweepAsync"/> at startup and every <see cref="GuestAdminService.SweepInterval"/>:
/// ends administrator windows whose VM stopped, and disables the accounts of ended windows (including those an
/// earlier run left open) once their guests answer.
/// </summary>
internal sealed class GuestAdminMonitorService(GuestAdminService guestAdmin, TimeProvider time, ILogger<GuestAdminMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(GuestAdminService.SweepInterval, time);
        do
        {
            try
            {
                await guestAdmin.SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not check administrator windows.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
