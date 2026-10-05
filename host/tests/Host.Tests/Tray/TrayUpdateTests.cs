using System.IO.Pipes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray's update section over a uniquely named pipe, on a host without the update service.</summary>
public sealed class TrayUpdateTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private UpdateSettings _settings = null!;
    private TrayPipeServer _server = null!;

    public async Task InitializeAsync()
    {
        var users = new UserStore(_dataDirectory);
        users.GetOrCreateDefault();
        _settings = new UpdateSettings(new UpdateOptions(), new HostSettingsStore(_dataDirectory));
        var services = new ServiceCollection()
            .AddSingleton(users)
            .AddSingleton<IAuditLog>(new FileAuditLog(_dataDirectory))
            .AddSingleton(_settings)
            .BuildServiceProvider();
        _server = new TrayPipeServer(new PairedDeviceStore(_dataDirectory, users), services, NullLogger<TrayPipeServer>.Instance, _pipeName);
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
    public async Task Connect_ReportsTheUpdateStatus()
    {
        await using var tray = await ConnectAsync();

        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        var status = Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync()).Status;
        Assert.False(status.Supported);
        Assert.Equal("stable", status.Channel);
        Assert.Equal(["beta", "stable"], status.Channels);
    }

    [Fact]
    public async Task SetUpdateChannel_SavesAKnownChannel_AndIgnoresAnUnknownOne()
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetUpdateChannelMessage("beta"));
        Assert.Equal("beta", Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync()).Status.Channel);

        await tray.SendAsync(new SetUpdateChannelMessage("nightly"));
        Assert.Equal("beta", Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync()).Status.Channel);
        Assert.Equal("beta", _settings.Current().Channel);
    }

    [Fact]
    public async Task CheckAndInstall_OnAHostWithoutTheUpdateService_AnswerWithTheStatus()
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new CheckForUpdateMessage());
        Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync());
        await tray.SendAsync(new InstallUpdateMessage());
        Assert.False(Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync()).Status.Supported);
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
        Assert.IsType<UpdateStatusMessage>(await tray.ReceiveAsync());
        return tray;
    }
}
