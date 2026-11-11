using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Cumulative processor times from GetSystemTimes, in 100-nanosecond units. Kernel time includes idle time.</summary>
public readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

/// <summary>The host's physical cores and how busy its processors are, for the host resources figures.</summary>
public interface IHostProcessorLoad
{
    /// <summary>Physical cores across all processors; null when Windows cannot say.</summary>
    int? PhysicalCoreCount { get; }

    /// <summary>Processor use across all logical processors, 0 to 100; null when it cannot be read.</summary>
    Task<double?> ReadUsagePercentAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Processor use from the change in system times. A reading taken between <see cref="MinimumSpan"/> and
/// <see cref="MaximumAge"/> after the previous one is measured against it, so a client that refreshes every few seconds
/// sees the use over that interval and the request does not wait. Otherwise the request waits
/// <see cref="MinimumSpan"/> for a second reading, because a span much longer than that is an average, not current use.
/// On client editions of Windows Hyper-V runs guest processors as threads of the host (the root scheduler), so the
/// figure includes what running VMs use.
/// </summary>
public sealed class HostProcessorLoad : IHostProcessorLoad
{
    public static readonly TimeSpan MinimumSpan = TimeSpan.FromMilliseconds(500);

    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    private readonly TimeProvider _time;
    private readonly Func<SystemTimes?> _readTimes;
    private readonly Lazy<int?> _cores;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (SystemTimes Times, DateTimeOffset At)? _previous;

    public HostProcessorLoad(TimeProvider time)
        : this(time, ReadSystemTimes, CountPhysicalCores)
    {
    }

    public HostProcessorLoad(TimeProvider time, Func<SystemTimes?> readTimes, Func<int?> countCores)
    {
        _time = time;
        _readTimes = readTimes;
        _cores = new Lazy<int?>(countCores);
    }

    public int? PhysicalCoreCount => _cores.Value;

    public async Task<double?> ReadUsagePercentAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (_readTimes() is not { } current)
            {
                return null;
            }

            if (_previous is { } previous && now - previous.At >= MinimumSpan && now - previous.At <= MaximumAge)
            {
                _previous = (current, now);
                return Usage(previous.Times, current);
            }

            await Task.Delay(MinimumSpan, _time, cancellationToken).ConfigureAwait(false);
            if (_readTimes() is not { } next)
            {
                return null;
            }

            _previous = (next, _time.GetUtcNow());
            return Usage(current, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The busy share of the time between two readings, rounded to a tenth of a percent.</summary>
    public static double? Usage(SystemTimes before, SystemTimes after)
    {
        var total = (after.Kernel - before.Kernel) + (after.User - before.User);
        if (total == 0 || after.Idle < before.Idle)
        {
            return null;
        }

        var busy = 100.0 * (total - Math.Min(total, after.Idle - before.Idle)) / total;
        return Math.Round(Math.Clamp(busy, 0, 100), 1);
    }

    private static SystemTimes? ReadSystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? new SystemTimes(idle, kernel, user) : null;

    /// <summary>One RelationProcessorCore record per physical core, from GetLogicalProcessorInformationEx.</summary>
    private static int? CountPhysicalCores()
    {
        uint length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length))
            {
                return null;
            }

            var cores = 0;
            for (var offset = 0; offset < length;)
            {
                // Each record starts with its relationship (4 bytes) and its size (4 bytes).
                var size = Marshal.ReadInt32(buffer, offset + 4);
                if (size <= 0)
                {
                    break;
                }

                cores++;
                offset += size;
            }

            return cores > 0 ? cores : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
}
