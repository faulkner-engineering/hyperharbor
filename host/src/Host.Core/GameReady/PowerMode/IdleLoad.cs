using System.Runtime.InteropServices;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.GameReady.PowerMode;

/// <summary>The host's utilization over a short sample. A value is null when it could not be measured.</summary>
public sealed record IdleLoad(double? CpuPercent, double? GpuPercent, DateTimeOffset MeasuredAt);

public interface IIdleLoadReader
{
    Task<IdleLoad> SampleAsync(CancellationToken cancellationToken);
}

/// <summary>One "GPU Engine" performance counter instance, for example "pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D".</summary>
public sealed record GpuEngineSample(string Instance, double Percent);

public static class GpuLoad
{
    /// <summary>
    /// Task Manager's figure: the utilization of each engine type (3D, Copy, VideoDecode, ...) is the sum over all
    /// processes, and the GPU's load is the busiest engine type.
    /// </summary>
    public static double? Of(IEnumerable<GpuEngineSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in samples)
        {
            var marker = sample.Instance.LastIndexOf("engtype_", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
            {
                continue;
            }

            var type = sample.Instance[(marker + "engtype_".Length)..];
            byType[type] = byType.GetValueOrDefault(type) + sample.Percent;
        }

        return byType.Count == 0 ? null : Math.Round(Math.Min(100, byType.Values.Max()), 1);
    }
}

/// <summary>CPU from the system times over one second, GPU from the "GPU Engine" counters (absent without a WDDM 2 GPU).</summary>
public sealed class WindowsIdleLoadReader(TimeProvider time) : IIdleLoadReader
{
    public async Task<IdleLoad> SampleAsync(CancellationToken cancellationToken)
    {
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);

        // Kernel time includes idle time.
        var total = (kernel2 - kernel1) + (user2 - user1);
        double? cpu = total == 0 ? null : Math.Round(100.0 * (total - (idle2 - idle1)) / total, 1);
        return new IdleLoad(cpu, await Task.Run(ReadGpu, cancellationToken).ConfigureAwait(false), time.GetUtcNow());
    }

    private static double? ReadGpu()
    {
        try
        {
            using var session = CimSession.Create(null);
            var samples = new List<GpuEngineSample>();
            foreach (var instance in session.QueryInstances(@"root\cimv2", "WQL", "SELECT Name, UtilizationPercentage FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine"))
            {
                using (instance)
                {
                    if (instance.CimInstanceProperties["Name"]?.Value is string name && instance.CimInstanceProperties["UtilizationPercentage"]?.Value is { } value)
                    {
                        samples.Add(new GpuEngineSample(name, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)));
                    }
                }
            }

            return GpuLoad.Of(samples);
        }
        catch (CimException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}

/// <summary>
/// Whether a game runs in the foreground on the signed-in user's desktop. The service cannot see that desktop, so
/// the tray reports it. A report that stops arriving (the tray exited) counts as no game.
/// </summary>
public sealed class ForegroundGameTracker(TimeProvider time)
{
    /// <summary>The tray reports about every 15 seconds; a report older than this is ignored.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private bool _active;
    private DateTimeOffset _reportedAt;

    public void Report(bool active)
    {
        lock (_gate)
        {
            _active = active;
            _reportedAt = time.GetUtcNow();
        }
    }

    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _active && time.GetUtcNow() - _reportedAt < MaximumAge;
            }
        }
    }
}
