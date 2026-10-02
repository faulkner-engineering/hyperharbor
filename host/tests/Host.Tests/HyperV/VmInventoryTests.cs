using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;

namespace HyperHarbor.Host.Tests.HyperV;

public class VmInventoryTests
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    [Fact]
    public async Task GetAsync_ReturnsMatchingVm()
    {
        var inventory = new VmInventory(new FakeReader(Snapshot()));

        var vm = await inventory.GetAsync(VmId, CancellationToken.None);

        Assert.NotNull(vm);
        Assert.Equal("Dev Workstation", vm.Name);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForUnknownVm()
    {
        var inventory = new VmInventory(new FakeReader(Snapshot()));

        Assert.Null(await inventory.GetAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ListAsync_PropagatesUnavailableException()
    {
        var inventory = new VmInventory(new ThrowingReader());

        await Assert.ThrowsAsync<HyperVUnavailableException>(() => inventory.ListAsync(CancellationToken.None));
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
