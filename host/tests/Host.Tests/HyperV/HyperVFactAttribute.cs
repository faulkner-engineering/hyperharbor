using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Tests.HyperV;

/// <summary>
/// A fact that runs only when the local Hyper-V management namespace can be queried.
/// </summary>
public sealed class HyperVFactAttribute : FactAttribute
{
    private static readonly Lazy<string?> UnavailableReason = new(Probe);

    public HyperVFactAttribute()
    {
        if (UnavailableReason.Value is { } reason)
        {
            Skip = reason;
        }
    }

    private static string? Probe()
    {
        try
        {
            using var session = CimSession.Create(null);
            _ = session.QueryInstances(@"root\virtualization\v2", "WQL", "SELECT Name FROM Msvm_VirtualSystemManagementService").ToList();
            return null;
        }
        catch (CimException ex)
        {
            return $"Hyper-V is not available to this account ({ex.NativeErrorCode}).";
        }
    }
}
