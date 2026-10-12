using System.Globalization;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Service.GameReady;

/// <summary>
/// The share of the GPU the ambient screen's processes use, from the "GPU Engine" counters (the instance name starts
/// with pid_&lt;id&gt;). The formatted counters need an earlier sample to compute a rate, so the query runs twice, a second apart,
/// and the second one counts. Null when Windows has no such counters.
/// </summary>
internal static class AmbientGpuLoad
{
    public static double? Of(IReadOnlySet<int> processIds)
    {
        ArgumentNullException.ThrowIfNull(processIds);
        try
        {
            using var session = CimSession.Create(null);
            _ = Read(session, processIds);
            Thread.Sleep(TimeSpan.FromSeconds(1));
            return Read(session, processIds);
        }
        catch (CimException)
        {
            return null;
        }
    }

    private static double? Read(CimSession session, IReadOnlySet<int> processIds)
    {
        var samples = new List<GpuEngineSample>();
        foreach (var instance in session.QueryInstances(@"root\cimv2", "WQL", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
        {
            using (instance)
            {
                if (instance.CimInstanceProperties["Name"]?.Value is string name
                    && instance.CimInstanceProperties["UtilizationPercentage"]?.Value is { } value
                    && ProcessOf(name) is { } id
                    && processIds.Contains(id))
                {
                    samples.Add(new GpuEngineSample(name, Convert.ToDouble(value, CultureInfo.InvariantCulture)));
                }
            }
        }

        // No engine of these processes in the list means the GPU did nothing for them.
        return samples.Count == 0 ? 0 : GpuLoad.Of(samples);
    }

    /// <summary>The process id in an instance name such as "pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D".</summary>
    internal static int? ProcessOf(string instance)
    {
        const string prefix = "pid_";
        if (!instance.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var end = instance.IndexOf('_', prefix.Length);
        return end > prefix.Length && int.TryParse(instance.AsSpan(prefix.Length, end - prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
