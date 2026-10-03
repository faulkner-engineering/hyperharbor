using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core;

/// <summary>
/// Read-only view of the virtual machines on this host.
/// </summary>
public interface IVmInventory
{
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be queried.</exception>
    Task<IReadOnlyList<Vm>> ListAsync(CancellationToken cancellationToken);

    /// <returns>The virtual machine, or null when no virtual machine has the given ID.</returns>
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be queried.</exception>
    Task<Vm?> GetAsync(Guid vmId, CancellationToken cancellationToken);

    /// <summary>The virtual machine's name, without the slower probes <see cref="GetAsync"/> runs.</summary>
    /// <returns>The name, or null when no virtual machine has the given ID.</returns>
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be queried.</exception>
    Task<string?> FindNameAsync(Guid vmId, CancellationToken cancellationToken);
}
