using HyperHarbor.Host.Core.Transfer;

namespace HyperHarbor.Host.Service.Transfer;

/// <summary>
/// At startup, takes back a TPM grant an interrupted outgoing transfer left and ends incoming transfers whose ticket the
/// restart lost; then, every minute, ends incoming transfers that went idle.
/// </summary>
internal sealed class TransferMaintenanceService(
    TransferSender sender, TransferReceiver receiver, TimeProvider time, ILogger<TransferMaintenanceService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await sender.RecoverAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not recover interrupted outgoing transfers.");
        }

        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await receiver.ExpireIdleAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Could not end idle incoming transfers.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
