using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// HyperHarbor.Host.exe is a Windows (GUI) executable, so it starts without a console. Commands that print
/// attach to the terminal they were started from, and a console run of the host opens its own window
/// (it outlives the terminal that started it, as the separate console executable used to).
/// </summary>
internal static class ConsoleAttachment
{
    private const int StandardOutput = -11;
    private const uint AttachParentProcess = uint.MaxValue;

    /// <summary>True when output already goes somewhere: redirected by a script or test, or inherited.</summary>
    public static bool HasStandardOutput
    {
        get
        {
            var handle = NativeMethods.GetStdHandle(StandardOutput);
            return handle != IntPtr.Zero && handle != new IntPtr(-1);
        }
    }

    /// <summary>Attaches to the parent's console. False when the parent has none (Explorer).</summary>
    public static bool AttachToParent() => NativeMethods.AttachConsole(AttachParentProcess);

    public static void Detach() => NativeMethods.FreeConsole();

    /// <summary>Opens a new console window for this process.</summary>
    public static void OpenWindow() => NativeMethods.AllocConsole();

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AllocConsole();
    }
}
