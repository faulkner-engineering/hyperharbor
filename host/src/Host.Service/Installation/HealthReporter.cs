using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Service.Api;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// Writes update\health.json once the host listens and removes it when the host stops, for the update
/// helper's health check (<see cref="HealthReport"/>).
/// </summary>
internal sealed class HealthReporter(IHostApplicationLifetime lifetime, IOptions<ApiOptions> api, IConfiguration configuration, TimeProvider time, ILogger<HealthReporter> logger)
    : IHostedService
{
    private readonly string _dataDirectory = configuration["DataDirectory"] is { Length: > 0 } configured ? configured : Core.Identity.HostIdentityStore.DefaultDataDirectory;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lifetime.ApplicationStarted.Register(Report);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Try(() => HealthReport.Delete(_dataDirectory));
        return Task.CompletedTask;
    }

    private void Report()
    {
        var address = string.IsNullOrEmpty(api.Value.ListenAddress) ? System.Net.IPAddress.Loopback.ToString() : api.Value.ListenAddress;
        Try(() => new HealthReport(HostVersion.Current.ToString(), Environment.ProcessId, address, api.Value.Port, time.GetUtcNow()).Write(_dataDirectory));
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The health report for the update helper could not be written.");
        }
    }
}
