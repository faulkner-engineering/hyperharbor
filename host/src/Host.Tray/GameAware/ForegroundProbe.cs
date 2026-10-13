using System.Runtime.InteropServices;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.GameAware;

/// <summary>
/// Reads what is in the foreground on this desktop: the notification state, whether the window fills its monitor and has a
/// frame, which program owns it, and the programs that started that one. The service decides what it means.
/// </summary>
internal static class ForegroundProbe
{
    private const int GwlStyle = -16;
    private const long WsCaption = 0x00C00000;
    private const long WsThickFrame = 0x00040000;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SnapshotProcess = 2;
    private const int MaxLineage = 8;

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
    };

    /// <summary>Null when nothing can be read (the desktop is locked or no window has the focus).</summary>
    public static ForegroundSignals? Read()
    {
        var window = GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(window, out var processId);
        var processes = ProcessTable();
        var name = processes.TryGetValue(processId, out var entry) ? entry.Name : null;
        var lineage = new List<string>();
        var seen = new HashSet<uint> { processId };
        var parent = entry.ParentId;
        while (lineage.Count < MaxLineage && parent != 0 && seen.Add(parent) && processes.TryGetValue(parent, out var up))
        {
            lineage.Add(up.Name);
            parent = up.ParentId;
        }

        var className = new System.Text.StringBuilder(64);
        _ = GetClassName(window, className, className.Capacity);
        var shell = window == GetShellWindow() || window == GetDesktopWindow() || ShellClasses.Contains(className.ToString());
        var own = processId == (uint)Environment.ProcessId || name?.StartsWith("hyperharbor", StringComparison.Ordinal) == true;
        var state = SHQueryUserNotificationState(out var notification) == 0 ? notification : (int?)null;
        return new ForegroundSignals(state, CoversMonitor(window), HasBorder(window), shell, own, name, lineage);
    }

    private static bool CoversMonitor(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info) || !GetWindowRect(window, out var rect))
        {
            return false;
        }

        return rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    private static bool HasBorder(IntPtr window)
    {
        var style = GetWindowLongPtr(window, GwlStyle).ToInt64();
        return (style & WsCaption) == WsCaption || (style & WsThickFrame) != 0;
    }

    /// <summary>Every process by id, with its lower case name (no ".exe") and its parent's id.</summary>
    private static Dictionary<uint, (string Name, uint ParentId)> ProcessTable()
    {
        var table = new Dictionary<uint, (string, uint)>();
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcess, 0);
        if (snapshot == new IntPtr(-1))
        {
            return table;
        }

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var more = Process32First(snapshot, ref entry); more; more = Process32Next(snapshot, ref entry))
            {
                var file = entry.ExeFile.ToLowerInvariant();
                table[entry.ProcessId] = (file.EndsWith(".exe", StringComparison.Ordinal) ? file[..^4] : file, entry.ParentProcessId);
            }
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        return table;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
