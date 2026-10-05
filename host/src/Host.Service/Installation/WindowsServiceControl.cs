using System.Diagnostics;
using System.Net;
using System.ServiceProcess;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>Starts and stops the installed HyperHarborHost service, for the update helper.</summary>
internal sealed class WindowsServiceControl : IServiceControl
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(120);

    public bool IsRunning
    {
        get
        {
            using var service = new ServiceController(HostService.Name);
            return service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var service = new ServiceController(HostService.Name);
        try
        {
            if (service.Status is ServiceControllerStatus.Running)
            {
                return;
            }

            if (service.Status is not ServiceControllerStatus.StartPending)
            {
                service.Start();
            }

            service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
        }
        catch (Exception ex) when (ex is System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"The {HostService.Name} service did not start: {ex.Message}", ex);
        }
    }, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        using var service = new ServiceController(HostService.Name);
        try
        {
            if (service.Status is ServiceControllerStatus.Stopped)
            {
                return;
            }

            if (service.Status is not ServiceControllerStatus.StopPending)
            {
                service.Stop();
            }

            service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
        }
        catch (Exception ex) when (ex is System.ServiceProcess.TimeoutException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"The {HostService.Name} service did not stop: {ex.Message}", ex);
        }
    }, cancellationToken);
}

/// <summary>
/// Healthy means: the service is running, health.json reports the expected version from a live process
/// that started after the update began, and the API port serves the host certificate.
/// </summary>
internal sealed class HostHealthProbe(string dataDirectory, WindowsServiceControl service) : IHealthProbe
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public async Task<bool> WaitHealthyAsync(SemanticVersion expected, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (!service.IsRunning)
                {
                    return false;
                }

                if (HealthReport.Read(dataDirectory) is { } report &&
                    report.Version == expected.ToString() &&
                    report.StartedAt >= since &&
                    IsAlive(report.ProcessId) &&
                    IPAddress.TryParse(report.Address, out var address) &&
                    await ServesHostCertificateAsync(address, report.Port, deadline.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(PollInterval, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task<bool> ServesHostCertificateAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var certificate = new HostCertificateStore(dataDirectory, Environment.MachineName).GetOrCreate();
            await HostTlsCheck.HandshakeAsync(address, port, certificate, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
