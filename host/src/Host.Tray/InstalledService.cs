using System.Runtime.InteropServices;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>The state of the Windows service Install-HyperHarbor.ps1 registers, read from the service control manager.</summary>
internal static class InstalledService
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceRunning = 0x00000004;

    /// <summary>
    /// True when the installed host service is running. It shuts Performance mode VMs down itself on
    /// SERVICE_CONTROL_PRESHUTDOWN, so the tray does not need to hold up a Windows shutdown.
    /// </summary>
    public static bool IsRunning()
    {
        var manager = NativeMethods.OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var service = NativeMethods.OpenService(manager, HostService.Name, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return NativeMethods.QueryServiceStatus(service, out var status) && status.CurrentState == ServiceRunning;
            }
            finally
            {
                NativeMethods.CloseServiceHandle(service);
            }
        }
        finally
        {
            NativeMethods.CloseServiceHandle(manager);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
        public static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
        public static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
