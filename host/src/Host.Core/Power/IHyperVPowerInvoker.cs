using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Sends a power request to Hyper-V. Returns once Hyper-V has accepted the request; the state
/// change itself may still be in progress.
/// </summary>
public interface IHyperVPowerInvoker
{
    /// <exception cref="VmNotFoundException">The virtual machine does not exist.</exception>
    /// <exception cref="VmActionNotAllowedException">Hyper-V reported the action is invalid in the current state.</exception>
    /// <exception cref="HyperVOperationException">Hyper-V rejected the request.</exception>
    /// <exception cref="HyperV.HyperVUnavailableException">Hyper-V cannot be reached.</exception>
    Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken);
}
