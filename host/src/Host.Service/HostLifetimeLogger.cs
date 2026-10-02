namespace HyperHarbor.Host.Service;

/// <summary>
/// Logs service start and stop. Placeholder until the API and Hyper-V services are added.
/// </summary>
public sealed class HostLifetimeLogger : IHostedService
{
    private readonly ILogger<HostLifetimeLogger> _logger;

    public HostLifetimeLogger(ILogger<HostLifetimeLogger> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HyperHarbor host service started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("HyperHarbor host service stopping.");
        return Task.CompletedTask;
    }
}
