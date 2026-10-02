using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;

namespace HyperHarbor.Host.Tests.HyperV;

public class VmInventoryTests
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    [Fact]
    public async Task GetAsync_ReturnsMatchingVm()
    {
        var inventory = new VmInventory(new FakeReader(Snapshot()), new FakeRdpProbe());

        var vm = await inventory.GetAsync(VmId, CancellationToken.None);

        Assert.NotNull(vm);
        Assert.Equal("Dev Workstation", vm.Name);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownVm()
    {
        var inventory = new VmInventory(new FakeReader(Snapshot()), new FakeRdpProbe());

        Assert.Null(await inventory.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_PropagatesUnavailableException()
    {
        var inventory = new VmInventory(new ThrowingReader(), new FakeRdpProbe());

        await Assert.ThrowsAsync<HyperVUnavailableException>(() => inventory.ListAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ListAsync_RunningVm_UsesProbeForRdpAvailability(bool reachable)
    {
        var probe = new FakeRdpProbe { Reachable = reachable };
        var snapshot = new HyperVSnapshot(
            [new ComputerSystemRow(VmId, "Dev Workstation", 2, 1000)],
            [],
            [],
            [new GuestNetworkRow(VmId, ["192.168.0.50"])]);

        var vm = Assert.Single(await new VmInventory(new FakeReader(snapshot), probe).ListAsync(CancellationToken.None));

        Assert.Equal(reachable, vm.RdpAvailable);
        Assert.Equal(new Shared.Contracts.Vms.VmRemoteDesktop("192.168.0.50", reachable), vm.RemoteDesktop);
        Assert.Equal(["192.168.0.50"], probe.Probed);
    }

    [Fact]
    public async Task ListAsync_StoppedVm_IsNotProbed()
    {
        var probe = new FakeRdpProbe();

        await new VmInventory(new FakeReader(Snapshot()), probe).ListAsync(CancellationToken.None);

        Assert.Empty(probe.Probed);
    }

    private static HyperVSnapshot Snapshot() => new(
        [new ComputerSystemRow(VmId, "Dev Workstation", 3, 0)],
        [],
        [],
        []);

    private sealed class FakeReader(HyperVSnapshot snapshot) : IHyperVReader
    {
        public Task<HyperVSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
    }

    private sealed class ThrowingReader : IHyperVReader
    {
        public Task<HyperVSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken) =>
            throw new HyperVUnavailableException("Hyper-V is not enabled.");
    }
}
