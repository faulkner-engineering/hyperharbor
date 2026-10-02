using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core;

/// <summary>
/// Builds the contract view of virtual machines from a Hyper-V snapshot, then checks which running
/// VMs accept Remote Desktop connections.
/// </summary>
public sealed class VmInventory : IVmInventory
{
    private readonly IHyperVReader _reader;
    private readonly IRdpProbe _rdpProbe;

    public VmInventory(IHyperVReader reader, IRdpProbe rdpProbe)
    {
        _reader = reader;
        _rdpProbe = rdpProbe;
    }

    public async Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _reader.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var vms = VmMapper.Map(snapshot);
        return await Task.WhenAll(vms.Select(vm => WithRdpProbeAsync(vm, cancellationToken))).ConfigureAwait(false);
    }

    public async Task<Vm?> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vms = await ListAsync(cancellationToken).ConfigureAwait(false);
        return vms.FirstOrDefault(vm => vm.Id == vmId);
    }

    private async Task<Vm> WithRdpProbeAsync(Vm vm, CancellationToken cancellationToken)
    {
        if (vm.State != VmState.Running || vm.RemoteDesktop?.Address is not { } address)
        {
            return vm;
        }

        var reachable = await _rdpProbe.IsReachableAsync(address, cancellationToken).ConfigureAwait(false);
        return vm with { RdpAvailable = reachable, RemoteDesktop = new VmRemoteDesktop(address, reachable) };
    }
}
