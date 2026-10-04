using HyperHarbor.Host.Service;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Tests.Performance;

/// <summary>
/// The preshutdown hook relies on a private ServiceBase field; these tests fail loudly if a .NET update
/// renames it. The control itself only arrives under the Service Control Manager.
/// </summary>
public sealed class PreshutdownServiceLifetimeTests
{
    [Fact]
    public void Lifetime_AcceptsThePreshutdownControl()
    {
        using var lifetime = Create(_ => Task.CompletedTask);

        Assert.Equal(PreshutdownServiceLifetime.AcceptPreshutdown, PreshutdownServiceLifetime.AcceptedCommands(lifetime) & PreshutdownServiceLifetime.AcceptPreshutdown);
    }

    [Fact]
    public void Preshutdown_RunsTheVmShutdown_WithinTheTimeout()
    {
        CancellationToken received = default;
        using var lifetime = Create(token =>
        {
            received = token;
            return Task.CompletedTask;
        });

        lifetime.RunBeforeShutdown();

        Assert.True(received.CanBeCanceled);
        Assert.True(PreshutdownServiceLifetime.VmShutdownTimeout < PreshutdownServiceLifetime.PreshutdownTimeout);
    }

    [Fact]
    public void Preshutdown_SwallowsFailures_SoTheServiceStillStops()
    {
        using var lifetime = Create(_ => throw new InvalidOperationException("Hyper-V is not available."));

        lifetime.RunBeforeShutdown();
    }

    private static PreshutdownServiceLifetime Create(Func<CancellationToken, Task> beforeShutdown) => new(
        new HostingEnvironment { ApplicationName = "HyperHarbor.Tests", ContentRootPath = AppContext.BaseDirectory },
        new ApplicationLifetime(NullLogger<ApplicationLifetime>.Instance),
        NullLoggerFactory.Instance,
        Options.Create(new HostOptions()),
        Options.Create(new WindowsServiceLifetimeOptions { ServiceName = "HyperHarbor Test" }),
        beforeShutdown);
}
