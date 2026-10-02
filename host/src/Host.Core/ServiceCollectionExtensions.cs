using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
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
        services.AddSingleton<IRdpProbe>(provider => new TcpRdpProbe(provider.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IVmInventory, VmInventory>();
        return services;
    }

    /// <summary>
    /// Registers virtual machine power actions. Requires <see cref="AddVmInventory"/>.
    /// </summary>
    public static IServiceCollection AddVmPowerControl(this IServiceCollection services)
    {
        services.AddSingleton<IHyperVPowerInvoker, CimHyperVPowerInvoker>();
        services.AddSingleton<IVmPowerService, VmPowerService>();
        return services;
    }
}
