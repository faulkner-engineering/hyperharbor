using System.IO.Pipes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>Moving the ISO library and the VM storage folder from the tray, over a uniquely named pipe.</summary>
public sealed class TrayFolderTests : IAsyncLifetime
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private HostSettingsStore _settings = null!;
    private TrayPipeServer _server = null!;

    private string DefaultFolder => Path.Combine(_dataDirectory, "default-isos");

    public async Task InitializeAsync()
    {
        var users = new UserStore(_dataDirectory);
        users.GetOrCreateDefault();
        var devices = new PairedDeviceStore(_dataDirectory, users);
        _settings = new HostSettingsStore(_dataDirectory);
        var services = new ServiceCollection()
            .AddSingleton(users)
            .AddSingleton<IAuditLog>(new FileAuditLog(_dataDirectory))
            .AddSingleton(_settings)
            .AddSingleton(new IsoLibrary(() => _settings.IsoFolder ?? DefaultFolder))
            .AddSingleton(new VmStorageLocation(_settings, new LifecycleOptions(), new Lifecycle.FakeHyperVHost()))
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
    public async Task Connect_ReportsTheFolderInUse()
    {
        await using var tray = await ConnectAsync();

        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        Assert.Equal(DefaultFolder, Assert.IsType<IsoFolderMessage>(await tray.ReceiveAsync()).Folder);
        var vms = Assert.IsType<VmFolderMessage>(await tray.ReceiveAsync());
        Assert.Equal(@"C:\Hyper-V\Virtual Hard Disks", vms.Folder);
        Assert.True(vms.IsDefault);
    }

    [Fact]
    public async Task SetVmFolder_CreatesSavesAndReportsIt()
    {
        var folder = Path.Combine(_dataDirectory, "vms");
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetVmFolderMessage(folder));

        var reply = Assert.IsType<VmFolderMessage>(await tray.ReceiveAsync());
        Assert.Equal(folder, reply.Folder);
        Assert.False(reply.IsDefault);
        Assert.Null(reply.Error);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(folder, new HostSettingsStore(_dataDirectory).VmFolder);
        Assert.Contains("traySetVmFolder", await File.ReadAllTextAsync(Path.Combine(_dataDirectory, FileAuditLog.FileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\server\share\vms")]
    [InlineData("vms")]
    public async Task SetVmFolder_RefusesNetworkAndRelativeFolders(string folder)
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetVmFolderMessage(folder));

        var reply = Assert.IsType<VmFolderMessage>(await tray.ReceiveAsync());
        Assert.True(reply.IsDefault);
        Assert.NotNull(reply.Error);
        Assert.Null(_settings.VmFolder);
    }

    [Fact]
    public async Task SetIsoFolder_CreatesSavesAndReportsIt()
    {
        var folder = Path.Combine(_dataDirectory, "chosen");
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetIsoFolderMessage(folder));

        var reply = Assert.IsType<IsoFolderMessage>(await tray.ReceiveAsync());
        Assert.Equal(folder, reply.Folder);
        Assert.Null(reply.Error);
        Assert.True(Directory.Exists(folder));
        Assert.Equal(folder, new HostSettingsStore(_dataDirectory).IsoFolder);
        Assert.Contains("traySetIsoFolder", await File.ReadAllTextAsync(Path.Combine(_dataDirectory, FileAuditLog.FileName)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\\server\share\isos")]
    [InlineData(@"relative\isos")]
    [InlineData("")]
    public async Task NetworkOrRelativeFolders_AreRefused(string folder)
    {
        await using var tray = await ConnectAndSkipInitialAsync();

        await tray.SendAsync(new SetIsoFolderMessage(folder));

        var reply = Assert.IsType<IsoFolderMessage>(await tray.ReceiveAsync());
        Assert.Equal(DefaultFolder, reply.Folder);
        Assert.NotNull(reply.Error);
        Assert.Null(_settings.IsoFolder);
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
        Assert.IsType<IsoFolderMessage>(await tray.ReceiveAsync());
        Assert.IsType<VmFolderMessage>(await tray.ReceiveAsync());
        return tray;
    }
}
