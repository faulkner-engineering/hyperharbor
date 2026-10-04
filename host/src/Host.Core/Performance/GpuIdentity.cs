using System.Globalization;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>Vendor and device identity of host GPUs, and the arithmetic of partition shares.</summary>
public static class GpuIdentity
{
    /// <summary>The vendor from a PCI device ID such as "PCI\VEN_10DE&amp;DEV_2684&amp;..." (any case).</summary>
    public static GpuVendor VendorOf(string? pnpDeviceId)
    {
        var id = pnpDeviceId?.ToUpperInvariant() ?? string.Empty;
        return id.Contains("VEN_10DE", StringComparison.Ordinal) ? GpuVendor.Nvidia
            : id.Contains("VEN_1002", StringComparison.Ordinal) ? GpuVendor.Amd
            : id.Contains("VEN_8086", StringComparison.Ordinal) ? GpuVendor.Intel
            : GpuVendor.Other;
    }

    /// <summary>
    /// The PnP device instance ID behind a partitionable GPU. Msvm_PartitionableGpu.Name is a device
    /// interface path, "\\?\PCI#VEN_8086&amp;DEV_9B41&amp;...#3&amp;11583659&amp;0&amp;10#{interface GUID}\GPUPARAV",
    /// whose first three '#' parts are the instance ID "PCI\VEN_8086&amp;DEV_9B41&amp;...\3&amp;11583659&amp;0&amp;10".
    /// </summary>
    public static string? DeviceInstanceId(string partitionableGpuName)
    {
        var path = partitionableGpuName.StartsWith(@"\\?\", StringComparison.Ordinal) ? partitionableGpuName[4..] : partitionableGpuName;
        var parts = path.Split('#');
        return parts.Length >= 3 ? $@"{parts[0]}\{parts[1]}\{parts[2]}".ToUpperInvariant() : null;
    }

    /// <summary>
    /// <paramref name="percent"/> of <paramref name="maximum"/>, in the GPU's own relative units. Decimal
    /// arithmetic, because encode maximums are reported as UInt64.MaxValue.
    /// </summary>
    public static ulong Share(ulong maximum, int percent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(percent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        return percent == 100 ? maximum : (ulong)decimal.Floor((decimal)maximum * percent / 100m);
    }

    /// <summary>A driver version as it appears in Windows ("31.0.15.5222"), compared ordinally after trimming.</summary>
    public static bool SameVersion(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static ulong ToUInt64(object? value) =>
        value is null ? 0 : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
}
