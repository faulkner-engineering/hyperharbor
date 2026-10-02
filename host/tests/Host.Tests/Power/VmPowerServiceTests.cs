using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Power;

public class VmPowerServiceTests
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly FakeVmInventory _inventory = new();
    private readonly FakePowerInvoker _invoker;
    private readonly VmPowerService _service;

    public VmPowerServiceTests()
    {
        _invoker = new FakePowerInvoker(_inventory);
        _service = new VmPowerService(_inventory, _invoker, NullLogger<VmPowerService>.Instance);
    }

    [Fact]
    public async Task PerformAsync_InvokesActionAndReturnsUpdatedState()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _invoker.ResultingState = VmState.Starting;

        var result = await _service.PerformAsync(VmId, VmAction.Start, CancellationToken.None);

        Assert.Equal(new VmActionResult(VmId, VmAction.Start, true, VmState.Starting), result);
        Assert.Equal([(VmId, VmAction.Start)], _invoker.Calls);
    }

    [Fact]
    public async Task PerformAsync_UnknownVm_ThrowsNotFoundWithoutInvoking()
    {
        await Assert.ThrowsAsync<VmNotFoundException>(
            () => _service.PerformAsync(VmId, VmAction.Start, CancellationToken.None));

        Assert.Empty(_invoker.Calls);
    }

    [Fact]
    public async Task PerformAsync_InvalidTransition_ThrowsWithoutInvoking()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));

        var ex = await Assert.ThrowsAsync<VmActionNotAllowedException>(
            () => _service.PerformAsync(VmId, VmAction.TurnOff, CancellationToken.None));

        Assert.Equal(VmState.Off, ex.State);
        Assert.Empty(_invoker.Calls);
    }

    [Fact]
    public async Task PerformAsync_PropagatesInvokerFailure()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Running));
        _invoker.ThrowOnInvoke = new HyperVOperationException("RequestStateChange", 32768);

        await Assert.ThrowsAsync<HyperVOperationException>(
            () => _service.PerformAsync(VmId, VmAction.Save, CancellationToken.None));
    }
}
