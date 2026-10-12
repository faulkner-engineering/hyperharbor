using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>
/// Measures what the ambient screen costs: the processor time of the screensaver process and everything it starts
/// (the WebView2 processes), as a share of all processors, over a window that begins after the page has settled.
/// </summary>
internal sealed class AmbientMeter(TimeProvider time, int processors)
{
    /// <summary>The window starts this long after the screen did, so the page's start-up work is not counted.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(20);

    /// <summary>The first figure is reported after this long, then every <see cref="ReportEvery"/>.</summary>
    public static readonly TimeSpan FirstReport = TimeSpan.FromSeconds(60);

    public static readonly TimeSpan ReportEvery = TimeSpan.FromSeconds(60);

    private readonly DateTimeOffset _started = time.GetUtcNow();
    private DateTimeOffset? _windowStart;
    private ulong _windowTicks;
    private DateTimeOffset _nextReport = time.GetUtcNow() + FirstReport;

    /// <summary>
    /// Call about every few seconds with the processes' total processor time (100 ns units).
    /// </summary>
    /// <returns>The cost over the window so far when a report is due, else null.</returns>
    public (double CpuPercent, int Seconds)? Tick(ulong cpuTicks)
    {
        var now = time.GetUtcNow();
        if (_windowStart is null)
        {
            if (now - _started >= Settle)
            {
                _windowStart = now;
                _windowTicks = cpuTicks;
            }

            return null;
        }

        if (now < _nextReport)
        {
            return null;
        }

        _nextReport = now + ReportEvery;
        var wall = now - _windowStart.Value;
        return (CpuPercent(cpuTicks - _windowTicks, wall, processors), (int)wall.TotalSeconds);
    }

    /// <summary>Processor time used over a wall-clock span, as a percentage of all processors, to one decimal.</summary>
    public static double CpuPercent(ulong cpuTicks, TimeSpan wall, int processors)
    {
        if (wall <= TimeSpan.Zero || processors < 1)
        {
            return 0;
        }

        return Math.Round(Math.Clamp(100.0 * cpuTicks / (wall.Ticks * (double)processors), 0, 100), 1);
    }
}

/// <summary>
/// A job object holding this process, so the processor time and process ids of the WebView2 processes it starts are
/// counted with it. Children created after the assignment join the job.
/// </summary>
internal sealed class JobAccounting : IDisposable
{
    private const int BasicAccountingInformation = 1;
    private const int BasicProcessIdList = 3;
    private const int MaxProcesses = 128;

    private readonly IntPtr _job;

    private JobAccounting(IntPtr job) => _job = job;

    /// <returns>The job holding this process, or null when Windows refuses (then only this process is counted).</returns>
    public static JobAccounting? TryCreate()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            return null;
        }

        if (!AssignProcessToJobObject(job, GetCurrentProcess()))
        {
            CloseHandle(job);
            return null;
        }

        return new JobAccounting(job);
    }

    /// <summary>User plus kernel time of every process in the job, in 100 ns units.</summary>
    public ulong CpuTicks()
    {
        var size = Marshal.SizeOf<BasicAccounting>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job, BasicAccountingInformation, buffer, size, out _))
            {
                return 0;
            }

            var info = Marshal.PtrToStructure<BasicAccounting>(buffer);
            return (ulong)(info.TotalUserTime + info.TotalKernelTime);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>The ids of the processes in the job now.</summary>
    public IReadOnlySet<int> ProcessIds()
    {
        var size = 8 + IntPtr.Size * MaxProcesses;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(_job, BasicProcessIdList, buffer, size, out _))
            {
                return new HashSet<int>();
            }

            var count = Marshal.ReadInt32(buffer, 4);
            var ids = new HashSet<int>();
            for (var index = 0; index < Math.Min(count, MaxProcesses); index++)
            {
                ids.Add((int)Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size));
            }

            return ids;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => CloseHandle(_job);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicAccounting
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, IntPtr info, int length, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
