using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Power;

public sealed class VmPowerService : IVmPowerService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVPowerInvoker _invoker;
    private readonly ILogger<VmPowerService> _logger;

    public VmPowerService(IVmInventory inventory, IHyperVPowerInvoker invoker, ILogger<VmPowerService> logger)
    {
        _inventory = inventory;
        _invoker = invoker;
        _logger = logger;
    }

    public async Task<VmActionResult> PerformAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
    {
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
