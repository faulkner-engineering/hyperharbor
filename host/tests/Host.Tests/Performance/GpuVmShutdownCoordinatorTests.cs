using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Performance;

public sealed class GpuVmShutdownCoordinatorTests : IDisposable
{
    private static readonly Guid GpuVm = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid PlainVm = Guid.Parse("9c8b7a65-4321-4fed-8cba-0987654321ab");
    private static readonly Guid OffGpuVm = Guid.Parse("4f1d2c3b-5a69-4788-9a0b-1c2d3e4f5a6b");

    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hh-gpu-shutdown-" + Guid.NewGuid().ToString("N"));
    private readonly FakeVmInventory _inventory = new();
    private readonly RecordingPower _power;
    private readonly PerformanceStore _store;
    private readonly List<AuditEntry> _audit = [];

    public GpuVmShutdownCoordinatorTests()
    {
        Directory.CreateDirectory(_dataDirectory);
        _store = new PerformanceStore(_dataDirectory);
        _power = new RecordingPower(_inventory);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(GpuVm, "Workstation", VmState.Running));
        _inventory.Vms.Add(FakeVmInventory.CreateVm(PlainVm, "Plain", VmState.Running));
        _inventory.Vms.Add(FakeVmInventory.CreateVm(OffGpuVm, "Spare", VmState.Off));
        foreach (var id in new[] { GpuVm, OffGpuVm })
        {
            _store.Save(new PerformanceRecord(id, new PerformanceSettings(4, 8192, null, null, null, null, false), DateTimeOffset.UtcNow, null));
        }
    }

    public void Dispose() => Directory.Delete(_dataDirectory, recursive: true);

    private GpuVmShutdownCoordinator Coordinator(Core.IVmInventory? inventory = null) =>
        new(inventory ?? _inventory, _power, _store, new ListAuditLog(_audit), TimeProvider.System, NullLogger<GpuVmShutdownCoordinator>.Instance);

    [Fact]
    public async Task Running_ListsOnlyRunningPerformanceModeVms()
    {
        var running = await Coordinator().RunningAsync(CancellationToken.None);

        Assert.Equal(["Workstation"], running.Select(vm => vm.Name));
    }

    [Fact]
    public async Task StopAll_ShutsDownOnlyRunningGpuVms_AndAuditsIt()
    {
        _power.TurnsOffOnShutdown = true;

        var result = await Coordinator().StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Equal([(GpuVm, VmAction.Shutdown)], _power.Calls);
        Assert.Equal(["Workstation"], result.Stopped);
        Assert.Empty(result.StillRunning);
        var entry = Assert.Single(_audit);
        Assert.Equal(("shutDownGpuVms", AuditOutcome.Succeeded), (entry.Action, entry.Outcome));
    }

    [Fact]
    public async Task StopAll_WaitsUntilTheVmIsOff()
    {
        // The guest takes until the second check to finish shutting down.
        var inventory = new OffOnReadInventory(_inventory, GpuVm, read: 3);

        var result = await Coordinator(inventory).StopAllAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Equal(["Workstation"], result.Stopped);
        Assert.True(inventory.Reads >= 3);
    }

    [Fact]
    public async Task StopAll_NeverTurnsAVmOff_WhenItDoesNotShutDownInTime()
    {
        var result = await Coordinator().StopAllAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(["Workstation"], result.StillRunning);
        Assert.DoesNotContain(_power.Calls, call => call.Action == VmAction.TurnOff);
        Assert.Equal(AuditOutcome.Failed, Assert.Single(_audit).Outcome);
    }

    [Fact]
    public async Task StopAll_KeepsGoing_WhenAGuestRefuses()
    {
        _power.Refuse = true;

        var result = await Coordinator().StopAllAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(["Workstation"], result.StillRunning);
    }

    [Fact]
    public async Task StopAll_WithNoGpuVmRunning_DoesNothing()
    {
        _inventory.SetState(GpuVm, VmState.Off);

        var result = await Coordinator().StopAllAsync(TimeSpan.FromSeconds(10), CancellationToken.None);

        Assert.Empty(result.Stopped);
        Assert.Empty(_power.Calls);
        Assert.Empty(_audit);
    }

    private sealed class RecordingPower(FakeVmInventory inventory) : IVmPowerService
    {
        public List<(Guid VmId, VmAction Action)> Calls { get; } = [];

        public bool TurnsOffOnShutdown { get; set; }

        public bool Refuse { get; set; }

        public Task<VmActionResult> PerformAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Calls.Add((vmId, action));
            if (Refuse)
            {
                throw new VmActionNotAllowedException(action, VmState.Running, "The guest is not responding.");
            }

            if (TurnsOffOnShutdown)
            {
                inventory.SetState(vmId, VmState.Off);
            }

            return Task.FromResult(new VmActionResult(vmId, action, true, VmState.Running));
        }
    }

    /// <summary>Reports the VM as off from the given read on.</summary>
    private sealed class OffOnReadInventory(FakeVmInventory inner, Guid vmId, int read) : Core.IVmInventory
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken)
        {
            if (++Reads >= read)
            {
                inner.SetState(vmId, VmState.Off);
            }

            return inner.ListAsync(cancellationToken);
        }

        public Task<Vm?> GetAsync(Guid id, CancellationToken cancellationToken) => inner.GetAsync(id, cancellationToken);

        public Task<string?> FindNameAsync(Guid id, CancellationToken cancellationToken) => inner.FindNameAsync(id, cancellationToken);
    }

    private sealed class ListAuditLog(List<AuditEntry> entries) : IAuditLog
    {
        public void Write(AuditEntry entry) => entries.Add(entry);
    }
}
