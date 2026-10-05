using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Holds up a Windows shutdown or restart while Performance mode VMs run, for a host service running
/// as a console app (a console app is not told about shutdowns). A GPU partition VM cannot be saved,
/// so Hyper-V would turn it off. The guard is a hidden top-level window: on WM_QUERYENDSESSION it shows
/// a reason on the "apps are preventing shutdown" screen, refuses, and asks the service to shut the VMs
/// down. Signing out is let through, because VMs keep running when the user signs out. The installed
/// service handles shutdowns itself (preshutdown), so the guard stands aside while it runs.
/// </summary>
internal sealed class ShutdownGuard : NativeWindow, IDisposable
{
    private const int WmQueryEndSession = 0x0011;
    private const long EndSessionLogoff = 0x80000000;

    /// <summary>Highest application shutdown level, so the tray is asked before other apps close.</summary>
    private const uint FirstApplicationShutdownLevel = 0x3FF;

    private readonly Func<IReadOnlyList<string>> _runningGpuVms;
    private readonly Action _stopGpuVms;
    private bool _blocking;

    /// <param name="runningGpuVms">The last known running Performance mode VMs; empty when the service is not connected.</param>
    /// <param name="stopGpuVms">Asks the service to shut them down.</param>
    public ShutdownGuard(Func<IReadOnlyList<string>> runningGpuVms, Action stopGpuVms)
    {
        _runningGpuVms = runningGpuVms;
        _stopGpuVms = stopGpuVms;
        NativeMethods.SetProcessShutdownParameters(FirstApplicationShutdownLevel, 0);
        CreateHandle(new CreateParams { Caption = "HyperHarbor shutdown guard" });
    }

    /// <summary>True while a shutdown is being held up.</summary>
    public bool Blocking => _blocking;

    /// <summary>The service finished shutting the VMs down: let the next shutdown through.</summary>
    public void Release()
    {
        if (_blocking)
        {
            NativeMethods.ShutdownBlockReasonDestroy(Handle);
            _blocking = false;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmQueryEndSession && (m.LParam.ToInt64() & EndSessionLogoff) == 0 && _runningGpuVms() is { Count: > 0 } running
            && !InstalledService.IsRunning())
        {
            if (!_blocking)
            {
                var names = string.Join(", ", running);
                var reason = running.Count == 1 ? $"Shutting down the GPU VM {names} first." : $"Shutting down {running.Count} GPU VMs first: {names}.";
                NativeMethods.ShutdownBlockReasonCreate(Handle, reason);
                _blocking = true;
                _stopGpuVms();
            }

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Release();
        DestroyHandle();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShutdownBlockReasonDestroy(IntPtr window);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessShutdownParameters(uint level, uint flags);
    }
}
