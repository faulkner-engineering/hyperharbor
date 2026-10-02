using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Validates and performs virtual machine power actions.
/// </summary>
public interface IVmPowerService
{
    /// <exception cref="VmNotFoundException">The virtual machine does not exist.</exception>
    /// <exception cref="VmActionNotAllowedException">The action is not valid in the current state.</exception>
    /// <exception cref="HyperVOperationException">Hyper-V rejected the request.</exception>
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be reached.</exception>
    Task<VmActionResult> PerformAsync(Guid vmId, VmAction action, CancellationToken cancellationToken);
}
