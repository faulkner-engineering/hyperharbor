using System.IO.Pipes;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>Setting the admin passphrase from the tray, over a uniquely named pipe.</summary>
public sealed class TrayAdminPassphraseTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private AdminPassphraseStore _store = null!;
    private TrayPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var users = new UserStore(_dataDirectory);
        users.GetOrCreateDefault();
        var devices = new PairedDeviceStore(_dataDirectory, users);
        _store = new AdminPassphraseStore(_dataDirectory);
        var services = new ServiceCollection()
            .AddSingleton(users)
            .AddSingleton<IAuditLog>(new FileAuditLog(_dataDirectory))
            .AddSingleton(new ElevationService(_store, devices, TimeProvider.System, NullLogger<ElevationService>.Instance))
            .BuildServiceProvider();
        _server = new TrayPipeServer(devices, services, NullLogger<TrayPipeServer>.Instance, _pipeName);
        await _server.StartAsync(_stop.Token);
    }

    public async Task DisposeAsync()
    {
        await _stop.CancelAsync();
        await _server.StopAsync(CancellationToken.None);
        _server.Dispose();
        _stop.Dispose();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Connect_ReportsThatNoPassphraseIsSet()
    {
        await using var tray = await ConnectAsync();

        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.False(Assert.IsType<AdminPassphraseStatusMessage>(await tray.ReceiveAsync()).Configured);
    }

    [Fact]
    public async Task SetAdminPassphrase_StoresTheHash_ReportsIt_AndAuditsIt()
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(AdminPassphrase.CreateHash("correct horse battery"));

        Assert.True(Assert.IsType<AdminPassphraseStatusMessage>(await tray.ReceiveAsync()).Configured);
        Assert.True(_store.Verify("correct horse battery"));
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(_dataDirectory, FileAuditLog.FileName)))!;
        Assert.Equal("traySetAdminPassphrase", (string?)entry["action"]);
        Assert.Equal("succeeded", (string?)entry["outcome"]);
    }

    [Fact]
    public async Task WeakHash_IsIgnored()
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(AdminPassphrase.CreateHash("correct horse battery", iterations: 1_000));
        await tray.SendAsync(new ListDevicesMessage());

        // The next reply is the device list, not a status change.
        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.False(_store.IsConfigured);
    }

    [Fact]
    public async Task MalformedHash_IsIgnored()
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetAdminPassphraseMessage(new byte[4], new byte[4], AdminPassphrase.DefaultIterations));
        await tray.SendAsync(new ListDevicesMessage());

        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.False(_store.IsConfigured);
    }

    private async Task<TrayPipeServerTests.TrayClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TrayPipeServerTests.Timeout);
        await pipe.ConnectAsync(timeout.Token);
        return new TrayPipeServerTests.TrayClient(pipe);
    }

    private async Task<TrayPipeServerTests.TrayClient> ConnectAndSkipInitialAsync()
    {
        var tray = await ConnectAsync();
        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.IsType<AdminPassphraseStatusMessage>(await tray.ReceiveAsync());
        return tray;
    }
}
