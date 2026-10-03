using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Power;

public sealed class VmPowerService : IVmPowerService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVPowerInvoker _invoker;
    private readonly VmOperationLocks _locks;
    private readonly ILogger<VmPowerService> _logger;

    public VmPowerService(IVmInventory inventory, IHyperVPowerInvoker invoker, VmOperationLocks locks, ILogger<VmPowerService> logger)
    {
        _inventory = inventory;
        _invoker = invoker;
        _locks = locks;
        _logger = logger;
    }

    public async Task<VmActionResult> PerformAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
    {
        // Held from the state check through the request, so a deletion or settings change cannot interleave.
        using var hold = _locks.Acquire(vmId, $"power action {action}");

        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false)
            ?? throw new VmNotFoundException(vmId);

        if (!VmActionPolicy.IsAllowed(vm.State, action))
        {
            throw new VmActionNotAllowedException(
                action,
                vm.State,
                $"Action {action} is not valid while the virtual machine is {vm.State}.");
        }

        _logger.LogInformation("Requesting {Action} for VM {Name} ({VmId}) in state {State}.", action, vm.Name, vmId, vm.State);
        await _invoker.InvokeAsync(vmId, action, cancellationToken).ConfigureAwait(false);

        // The request has been accepted, so report the current state even if the caller has since cancelled.
        var updated = await _inventory.GetAsync(vmId, CancellationToken.None).ConfigureAwait(false);
        return new VmActionResult(vmId, action, Accepted: true, updated?.State ?? vm.State);
    }
}
