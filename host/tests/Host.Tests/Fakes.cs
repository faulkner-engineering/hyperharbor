using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests;

/// <summary>In-memory inventory whose VM list tests can change.</summary>
internal sealed class FakeVmInventory : IVmInventory
{
    public List<Vm> Vms { get; } = [];

    public Exception? ThrowOnRead { get; set; }

    public Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return Task.FromResult<IReadOnlyList<Vm>>(Vms.ToList());
    }

    public async Task<Vm?> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vms = await ListAsync(cancellationToken);
        return vms.FirstOrDefault(vm => vm.Id == vmId);
    }

    public void SetState(Guid vmId, VmState state)
    {
        var index = Vms.FindIndex(vm => vm.Id == vmId);
        Vms[index] = Vms[index] with { State = state };
    }

    public static Vm CreateVm(Guid id, string name, VmState state, GuestOsFamily guestOs = GuestOsFamily.Windows, string? address = null) =>
        new(id, name, state, null, null, null, 2, false, address is null ? [] : [address], GuestOs: new VmGuestOs(guestOs, null));
}

/// <summary>Records power requests and optionally applies a resulting state to a <see cref="FakeVmInventory"/>.</summary>
internal sealed class FakePowerInvoker : IHyperVPowerInvoker
{
    private readonly FakeVmInventory? _inventory;

    public FakePowerInvoker(FakeVmInventory? inventory = null)
    {
        _inventory = inventory;
    }

    public List<(Guid VmId, VmAction Action)> Calls { get; } = [];

    public Exception? ThrowOnInvoke { get; set; }

    public VmState? ResultingState { get; set; }

    public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
    {
        Calls.Add((vmId, action));

        if (ThrowOnInvoke is not null)
        {
            throw ThrowOnInvoke;
        }

        if (_inventory is not null && ResultingState is { } state)
        {
            _inventory.SetState(vmId, state);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Reports a fixed Remote Desktop reachability and records probed addresses.</summary>
internal sealed class FakeRdpProbe : HyperHarbor.Host.Core.HyperV.IRdpProbe
{
    public bool Reachable { get; set; } = true;

    public List<string> Probed { get; } = [];

    public Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken)
    {
        lock (Probed)
        {
            Probed.Add(address);
        }

        return Task.FromResult(Reachable);
    }
}
