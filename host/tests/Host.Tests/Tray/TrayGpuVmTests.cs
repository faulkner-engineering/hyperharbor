using System.IO.Pipes;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Service.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>The tray's shutdown guard asking about and stopping the Performance mode VMs, over a uniquely named pipe.</summary>
public sealed class TrayGpuVmTests : IAsyncLifetime
{
    private static readonly Guid GpuVm = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly string _pipeName = "HyperHarbor.Tests." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private readonly FakeVmInventory _inventory = new();
    private readonly ShutDownPower _power;
    private TrayPipeServer _server = null!;

    public TrayGpuVmTests() => _power = new ShutDownPower(_inventory);

    public async Task InitializeAsync()
    {
        var users = new UserStore(_dataDirectory);
        users.GetOrCreateDefault();
        var devices = new PairedDeviceStore(_dataDirectory, users);
        var store = new PerformanceStore(_dataDirectory);
        store.Save(new PerformanceRecord(GpuVm, new PerformanceSettings(4, 8192, null, null, null, null, false), DateTimeOffset.UtcNow, null));
        _inventory.Vms.Add(FakeVmInventory.CreateVm(GpuVm, "Workstation", VmState.Running));
        var services = new ServiceCollection()
            .AddSingleton(users)
            .AddSingleton<IAuditLog>(new FileAuditLog(_dataDirectory))
            .AddSingleton(new GpuVmShutdownCoordinator(_inventory, _power, store, null, TimeProvider.System, NullLogger<GpuVmShutdownCoordinator>.Instance))
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
    public async Task Query_ListsTheRunningGpuVms()
    {
        await using var tray = await ConnectAsync();

        await tray.SendAsync(new GpuVmsQueryMessage());

        Assert.Equal(["Workstation"], Assert.IsType<GpuVmsMessage>(await tray.ReceiveAsync()).Running);
    }

    [Fact]
    public async Task Stop_ShutsThemDown_AndReportsIt()
    {
        await using var tray = await ConnectAsync();

        await tray.SendAsync(new StopGpuVmsMessage());

        var stopped = Assert.IsType<GpuVmsStoppedMessage>(await tray.ReceiveAsync());
        Assert.Equal(["Workstation"], stopped.Stopped);
        Assert.Empty(stopped.StillRunning);
        Assert.Equal([VmAction.Shutdown], _power.Actions);
    }

    private async Task<TrayPipeServerTests.TrayClient> ConnectAsync()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TrayPipeServerTests.Timeout);
        await pipe.ConnectAsync(timeout.Token);
        var tray = new TrayPipeServerTests.TrayClient(pipe);
        Assert.IsType<DeviceListMessage>(await tray.ReceiveAsync());
        return tray;
    }

    /// <summary>A guest that shuts down at once.</summary>
    private sealed class ShutDownPower(FakeVmInventory inventory) : IVmPowerService
    {
        public List<VmAction> Actions { get; } = [];

        public Task<VmActionResult> PerformAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            inventory.SetState(vmId, VmState.Off);
            return Task.FromResult(new VmActionResult(vmId, action, true, VmState.Off));
        }
    }
}
