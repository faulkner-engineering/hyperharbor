using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core;

/// <summary>
/// Builds the contract view of virtual machines from a Hyper-V snapshot.
/// </summary>
public sealed class VmInventory : IVmInventory
{
    private readonly IHyperVReader _reader;

    public VmInventory(IHyperVReader reader)
    {
        _reader = reader;
    }

    public async Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _reader.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return VmMapper.Map(snapshot);
    }

    public async Task<Vm?> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vms = await ListAsync(cancellationToken).ConfigureAwait(false);
        return vms.FirstOrDefault(vm => vm.Id == vmId);
    }
}
