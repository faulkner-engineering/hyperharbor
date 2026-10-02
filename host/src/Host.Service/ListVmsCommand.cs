using System.Text.Json;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Service;

/// <summary>
/// Diagnostic command that prints the virtual machine inventory as contract JSON and exits.
/// </summary>
internal static class ListVmsCommand
{
    public const string Switch = "--list-vms";

    private static readonly JsonSerializerOptions OutputOptions = new(ContractJson.Options) { WriteIndented = true };

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        var inventory = services.GetRequiredService<IVmInventory>();

        try
        {
            var vms = await inventory.ListAsync(CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(vms, OutputOptions));
            return 0;
        }
        catch (HyperVUnavailableException ex)
        {
            Console.Error.WriteLine($"Hyper-V is unavailable: {ex.Message}");
            return 2;
        }
    }
}
