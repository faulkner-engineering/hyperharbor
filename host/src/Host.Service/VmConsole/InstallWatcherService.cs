using HyperHarbor.Host.Core.Unattend;

namespace HyperHarbor.Host.Service.VmConsole;

/// <summary>Runs <see cref="UnattendedInstallWatcher"/> every Install:PollSeconds while the host runs.</summary>
internal sealed class InstallWatcherService : BackgroundService
{
    private readonly UnattendedInstallWatcher _watcher;
    private readonly InstallWatcherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InstallWatcherService> _logger;

    public InstallWatcherService(UnattendedInstallWatcher watcher, InstallWatcherOptions options, TimeProvider time, ILogger<InstallWatcherService> logger)
    {
        _watcher = watcher;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), _time);
        do
        {
            try
            {
                await _watcher.TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad pass must not stop the watcher; the next pass tries again.
                _logger.LogError(ex, "Checking unattended installs failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
