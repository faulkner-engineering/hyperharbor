using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;

namespace HyperHarbor.Host.Service;

/// <summary>
/// Logs the virtual machine inventory once at startup. A missing or inaccessible Hyper-V
/// installation is logged as a warning and does not stop the service.
/// </summary>
public sealed class VmInventoryStartupLogger : BackgroundService
{
    private readonly IVmInventory _inventory;
    private readonly ILogger<VmInventoryStartupLogger> _logger;

    public VmInventoryStartupLogger(IVmInventory inventory, ILogger<VmInventoryStartupLogger> logger)
    {
        _inventory = inventory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("HyperHarbor host service started.");

        try
        {
            var vms = await _inventory.ListAsync(stoppingToken);
            _logger.LogInformation("Found {Count} virtual machine(s).", vms.Count);

            foreach (var vm in vms)
            {
                _logger.LogInformation(
                    "VM {Name} ({Id}): state {State}, generation {Generation}, addresses [{Addresses}]",
                    vm.Name,
                    vm.Id,
                    vm.State,
                    vm.Generation,
                    string.Join(", ", vm.IpAddresses));
            }
        }
        catch (HyperVUnavailableException ex)
        {
            _logger.LogWarning("Hyper-V is unavailable: {Reason}", ex.Message);
        }
    }
}
