using System.Diagnostics;
using System.Runtime.InteropServices;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>Samples the host's memory in use and process count, before and after the Lean host action.</summary>
public interface IHostMetrics
{
    Task<HostLeanMetrics> SampleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Memory in use is physical memory minus what is available, in MB; the process count is every process in the
/// session list. The sample spans one second so the CPU load can be read: a busy CPU (25 percent or more) marks the
/// sample as not idle, and the page says so rather than pretending the numbers are comparable.
/// </summary>
public sealed class WindowsHostMetrics(TimeProvider time) : IHostMetrics
{
    private const double IdleCpuLimit = 25;

    public async Task<HostLeanMetrics> SampleAsync(CancellationToken cancellationToken)
    {
        GetSystemTimes(out var idle1, out var kernel1, out var user1);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        GetSystemTimes(out var idle2, out var kernel2, out var user2);

        // Kernel time includes idle time.
        var total = (kernel2 - kernel1) + (user2 - user1);
        var busy = total == 0 ? 0 : 100.0 * (total - (idle2 - idle1)) / total;

        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var usedMb = GlobalMemoryStatusEx(ref status) ? (int)((status.TotalPhys - status.AvailPhys) / (1024 * 1024)) : 0;

        var processes = Process.GetProcesses();
        try
        {
            return new HostLeanMetrics(time.GetUtcNow(), usedMb, processes.Length, busy < IdleCpuLimit);
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
}
