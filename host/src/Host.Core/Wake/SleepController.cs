using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Core.Wake;

public interface ISleepController
{
    /// <summary>Puts the computer to sleep (not hibernate). Returns when the computer resumes.</summary>
    void Sleep();
}

public sealed class WindowsSleepController : ISleepController
{
    public void Sleep()
    {
        if (!SetSuspendState(hibernate: false, forceCritical: false, disableWakeEvent: false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The computer could not be put to sleep.");
        }
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool SetSuspendState(
        [MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical,
        [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);
}
