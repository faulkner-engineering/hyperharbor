using System.Net.NetworkInformation;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Service.Backup;

/// <summary>Ticks <see cref="BackupCoordinator"/> every minute while the host runs.</summary>
internal sealed class BackupSchedulerService(BackupCoordinator coordinator, TimeProvider time, ILogger<BackupSchedulerService> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, time);
        do
        {
            try
            {
                await coordinator.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad pass (for example Hyper-V briefly unreachable) must not stop the scheduler.
                logger.LogError(ex, "Checking backups failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// A conservative signal for "someone is using this VM": an established Remote Desktop connection between the host and the
/// guest (the host's own maintenance sessions and the console tunnel). Remote Desktop from other PCs goes straight to the
/// guest and is not visible here, so a client's check-in is the other half of the picture (see the backup status text).
/// </summary>
internal sealed class TcpVmSessionProbe : IVmSessionProbe
{
    private const int RemoteDesktopPort = 3389;

    public Task<bool> HasActiveSessionAsync(Vm vm, CancellationToken cancellationToken)
    {
        if (vm.IpAddresses.Count == 0)
        {
            return Task.FromResult(false);
        }

        var addresses = vm.IpAddresses.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var active = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections().Any(connection =>
            connection.State == TcpState.Established
            && connection.RemoteEndPoint.Port == RemoteDesktopPort
            && addresses.Contains(connection.RemoteEndPoint.Address.ToString()));
        return Task.FromResult(active);
    }
}
