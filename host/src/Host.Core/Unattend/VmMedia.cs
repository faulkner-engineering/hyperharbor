using HyperHarbor.Host.Core.HyperV;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>Removes an ISO from a VM's DVD drive.</summary>
public interface IVmMedia
{
    /// <summary>Removes <paramref name="isoPath"/> from whichever DVD drive holds it. Does nothing when none does.</summary>
    Task EjectAsync(Guid vmId, string isoPath, CancellationToken cancellationToken);
}

/// <summary>
/// RemoveResourceSettings on the DVD's media (Msvm_StorageAllocationSettingData). The drive stays, empty.
/// DVD media can be removed while the VM runs.
/// </summary>
public sealed class CimVmMedia : IVmMedia
{
    public Task EjectAsync(Guid vmId, string isoPath, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            var media = CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData");
            try
            {
                var inserted = media.FirstOrDefault(item =>
                    item.CimInstanceProperties["HostResource"]?.Value is string[] paths
                    && paths.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(isoPath), StringComparison.OrdinalIgnoreCase)));
                if (inserted is null)
                {
                    return;
                }

                using var service = HyperVCim.ManagementService(session);
                using var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("ResourceSettings", new[] { inserted }, CimType.ReferenceArray, CimFlags.In),
                };
                using var result = await HyperVCim.InvokeAsync(session, service, "RemoveResourceSettings", parameters, "removing the answer file DVD", cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                foreach (var item in media)
                {
                    item.Dispose();
                }
            }
        }, cancellationToken);
}
