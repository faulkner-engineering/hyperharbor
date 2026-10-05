using System.IO.Pipes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>
/// Stopping the server waits for messages being handled. An unpair sends the new device list before it writes
/// its audit entry, so a caller that stopped on seeing the list used to race the audit write (seen on CI as
/// "audit.log is being used by another process" while deleting the data folder).
/// </summary>
public sealed class TrayPipeServerStopTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_WaitsForAnUnpairToFinishItsAuditEntry()
    {
        var users = new UserStore(_dataDirectory);
        var devices = new PairedDeviceStore(_dataDirectory, users);
        var device = devices.Add(users.GetOrCreateDefault().UserId, "Laptop", new string('a', 64), DateTimeOffset.UtcNow);
        var audit = new SlowAuditLog();
        var services = new ServiceCollection().AddSingleton(users).AddSingleton<IAuditLog>(audit).BuildServiceProvider();
        using var stop = new CancellationTokenSource();
        using var server = new TrayPipeServer(devices, services, NullLogger<TrayPipeServer>.Instance, _pipeName);
        await server.StartAsync(stop.Token);

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using (var timeout = new CancellationTokenSource(TrayPipeServerTests.Timeout))
        {
            await pipe.ConnectAsync(timeout.Token);
        }

        await using var tray = new TrayPipeServerTests.TrayClient(pipe);
        await tray.ReceiveAsync();
        await tray.SendAsync(new RemoveDeviceMessage(device.DeviceId));
        Assert.Empty(Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync()).Devices);
        Assert.False(audit.Written);

        await stop.CancelAsync();
        await server.StopAsync(CancellationToken.None);

        Assert.True(audit.Written);
    }

    /// <summary>Holds each write long enough that a stop which does not wait would see it unfinished.</summary>
    private sealed class SlowAuditLog : IAuditLog
    {
        private int _written;

        public bool Written => Volatile.Read(ref _written) == 1;

        public void Write(AuditEntry entry)
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            Volatile.Write(ref _written, 1);
        }
    }
}
