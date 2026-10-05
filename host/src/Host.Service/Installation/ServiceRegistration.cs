using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>Registers the host with the service control manager: LocalSystem, automatic start, restart after a crash.</summary>
internal static class ServiceRegistration
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceWin32OwnProcess = 0x10;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceErrorNormal = 1;
    private const uint ConfigDescription = 1;
    private const uint ConfigFailureActions = 2;
    private const int ScActionRestart = 1;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDelete = 1072;

    /// <summary>Restart delays after the first, second, and later crashes; the count resets after a day.</summary>
    private static readonly TimeSpan[] RestartDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60)];
    private static readonly TimeSpan FailureCountReset = TimeSpan.FromDays(1);

    public static bool Exists(string name)
    {
        using var manager = Open(ScManagerConnect);
        using var service = NativeMethods.OpenService(manager, name, ServiceQueryConfig);
        return !service.IsInvalid;
    }

    /// <summary>Creates the service, or updates an existing one to these settings.</summary>
    public static void CreateOrUpdate(string name, string displayName, string description, string binaryPath)
    {
        using var manager = Open(ScManagerAllAccess);
        var service = NativeMethods.OpenService(manager, name, ServiceAllAccess);
        if (service.IsInvalid)
        {
            service.Dispose();
            service = NativeMethods.CreateService(
                manager, name, displayName, ServiceAllAccess, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
                binaryPath, null, IntPtr.Zero, null, null, null);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, error == ErrorServiceMarkedForDelete
                    ? $"The {name} service is still being removed. Close the Services window and try again."
                    : $"Could not create the {name} service.");
            }
        }
        else if (!NativeMethods.ChangeServiceConfig(
            service, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal, binaryPath, null, IntPtr.Zero, null, "LocalSystem", string.Empty, displayName))
        {
            var error = Marshal.GetLastWin32Error();
            service.Dispose();
            throw new Win32Exception(error, $"Could not update the {name} service.");
        }

        using (service)
        {
            var descriptionInfo = new ServiceDescription { Description = description };
            if (!NativeMethods.ChangeServiceConfig2(service, ConfigDescription, ref descriptionInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set the {name} service's description.");
            }

            SetFailureActions(service, name);
        }
    }

    /// <summary>Marks the service for deletion; it disappears once it has stopped and every handle is closed.</summary>
    public static void Delete(string name)
    {
        using var manager = Open(ScManagerAllAccess);
        using var service = NativeMethods.OpenService(manager, name, ServiceAllAccess);
        if (service.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorServiceDoesNotExist)
            {
                return;
            }

            throw new Win32Exception(error, $"Could not open the {name} service.");
        }

        if (!NativeMethods.DeleteService(service) && Marshal.GetLastWin32Error() is var deleteError && deleteError != ErrorServiceMarkedForDelete)
        {
            throw new Win32Exception(deleteError, $"Could not remove the {name} service.");
        }
    }

    private static void SetFailureActions(ServiceHandle service, string name)
    {
        var actions = RestartDelays.Select(delay => new ScAction { Type = ScActionRestart, Delay = (uint)delay.TotalMilliseconds }).ToArray();
        var size = Marshal.SizeOf<ScAction>();
        var buffer = Marshal.AllocHGlobal(size * actions.Length);
        try
        {
            for (var i = 0; i < actions.Length; i++)
            {
                Marshal.StructureToPtr(actions[i], buffer + (i * size), fDeleteOld: false);
            }

            var info = new ServiceFailureActions
            {
                ResetPeriod = (uint)FailureCountReset.TotalSeconds,
                ActionCount = (uint)actions.Length,
                Actions = buffer,
            };
            if (!NativeMethods.ChangeServiceConfig2(service, ConfigFailureActions, ref info))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not set the {name} service's recovery actions.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static ServiceHandle Open(uint access)
    {
        var manager = NativeMethods.OpenSCManager(null, null, access);
        if (manager.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            manager.Dispose();
            throw new Win32Exception(error, "Could not open the service control manager.");
        }

        return manager;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceDescription
    {
        public string Description;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ServiceFailureActions
    {
        public uint ResetPeriod;
        public string? RebootMessage;
        public string? Command;
        public uint ActionCount;
        public IntPtr Actions;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScAction
    {
        public int Type;
        public uint Delay;
    }

    private sealed class ServiceHandle : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenSCManagerW")]
        public static extern ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenServiceW")]
        public static extern ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateServiceW")]
        public static extern ServiceHandle CreateService(
            ServiceHandle manager, string serviceName, string displayName, uint desiredAccess, uint serviceType, uint startType,
            uint errorControl, string binaryPathName, string? loadOrderGroup, IntPtr tagId, string? dependencies,
            string? serviceStartName, string? password);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfigW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig(
            ServiceHandle service, uint serviceType, uint startType, uint errorControl, string binaryPathName,
            string? loadOrderGroup, IntPtr tagId, string? dependencies, string? serviceStartName, string password, string displayName);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, ref ServiceDescription info);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "ChangeServiceConfig2W")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, ref ServiceFailureActions info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteService(ServiceHandle service);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
