using HyperHarbor.Host.Core.HyperV;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the read-only Hyper-V virtual machine inventory.
    /// </summary>
    public static IServiceCollection AddVmInventory(this IServiceCollection services)
    {
        services.AddSingleton<IHyperVReader, CimHyperVReader>();
        services.AddSingleton<IVmInventory, VmInventory>();
        return services;
    }
}
