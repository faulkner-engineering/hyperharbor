using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using HyperHarbor.Host.Core.Performance;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Options;

namespace HyperHarbor.Host.Service;

/// <summary>
/// The Windows service lifetime, plus SERVICE_CONTROL_PRESHUTDOWN: before Windows shuts down, the
/// service shuts down the running Performance mode VMs (which cannot be saved) and only then stops.
/// .NET's ServiceBase has no preshutdown support, so the accepted-controls field is set through
/// reflection and the control arrives in OnCustomCommand. Used only when the process runs under the
/// Service Control Manager; there is no installer yet, so this has not run on a live host.
/// </summary>
public sealed class PreshutdownServiceLifetime : WindowsServiceLifetime
{
    /// <summary>SERVICE_CONTROL_PRESHUTDOWN.</summary>
    public const int PreshutdownControl = 0x0F;

    /// <summary>SERVICE_ACCEPT_PRESHUTDOWN.</summary>
    public const int AcceptPreshutdown = 0x100;

    /// <summary>ServiceBase's private field with the SERVICE_ACCEPT_* flags it reports to the SCM.</summary>
    internal const string AcceptedCommandsField = "_acceptedCommands";

    /// <summary>How long Windows waits for the service before shutting down anyway.</summary>
    public static readonly TimeSpan PreshutdownTimeout = TimeSpan.FromSeconds(120);

    /// <summary>The VMs get most of the time; the rest is for stopping the service.</summary>
    internal static readonly TimeSpan VmShutdownTimeout = PreshutdownTimeout - TimeSpan.FromSeconds(15);

    private readonly Func<CancellationToken, Task> _beforeShutdown;
    private readonly ILogger<PreshutdownServiceLifetime> _logger;

    public PreshutdownServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        GpuVmShutdownCoordinator coordinator)
        : this(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor,
            cancellationToken => coordinator.StopAllAsync(VmShutdownTimeout, cancellationToken))
    {
    }

    internal PreshutdownServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor,
        Func<CancellationToken, Task> beforeShutdown)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _beforeShutdown = beforeShutdown;
        _logger = loggerFactory.CreateLogger<PreshutdownServiceLifetime>();
        if (!TryAcceptPreshutdown(this))
        {
            _logger.LogWarning("This .NET version's ServiceBase has no {Field} field; Performance mode VMs will not be shut down before Windows shuts down.", AcceptedCommandsField);
        }
    }

    /// <summary>Adds SERVICE_ACCEPT_PRESHUTDOWN to the controls the service reports to the SCM.</summary>
    /// <returns>False when the field is missing, for example after a .NET change.</returns>
    internal static bool TryAcceptPreshutdown(ServiceBase service)
    {
        if (typeof(ServiceBase).GetField(AcceptedCommandsField, BindingFlags.Instance | BindingFlags.NonPublic) is not { FieldType: var type } field || type != typeof(int))
        {
            return false;
        }

        field.SetValue(service, (int)field.GetValue(service)! | AcceptPreshutdown);
        return true;
    }

    internal static int AcceptedCommands(ServiceBase service) =>
        (int)typeof(ServiceBase).GetField(AcceptedCommandsField, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    protected override void OnStart(string[] args)
    {
        base.OnStart(args);
        SetPreshutdownTimeout();
    }

    protected override void OnCustomCommand(int command)
    {
        if (command != PreshutdownControl)
        {
            base.OnCustomCommand(command);
            return;
        }

        RunBeforeShutdown();
        Stop();
    }

    /// <summary>Shuts the VMs down, never longer than the preshutdown timeout allows.</summary>
    internal void RunBeforeShutdown()
    {
        _logger.LogInformation("Windows is shutting down; shutting down the Performance mode VMs first.");
        try
        {
            using var timeout = new CancellationTokenSource(PreshutdownTimeout - TimeSpan.FromSeconds(5));
            _beforeShutdown(timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shutting down the Performance mode VMs before Windows shuts down failed.");
        }
    }

    /// <summary>Asks the SCM to wait up to <see cref="PreshutdownTimeout"/> for this service. Best effort.</summary>
    private void SetPreshutdownTimeout()
    {
        var manager = NativeMethods.OpenSCManagerW(null, null, NativeMethods.ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            _logger.LogWarning("Could not set the preshutdown timeout: {Error}", new Win32Exception().Message);
            return;
        }

        try
        {
            var service = NativeMethods.OpenServiceW(manager, ServiceName, NativeMethods.ServiceChangeConfig);
            if (service == IntPtr.Zero)
            {
                _logger.LogWarning("Could not set the preshutdown timeout: {Error}", new Win32Exception().Message);
                return;
            }

            try
            {
                var info = new NativeMethods.ServicePreshutdownInfo { Timeout = (uint)PreshutdownTimeout.TotalMilliseconds };
                if (!NativeMethods.ChangeServiceConfig2W(service, NativeMethods.ServiceConfigPreshutdownInfo, ref info))
                {
                    _logger.LogWarning("Could not set the preshutdown timeout: {Error}", new Win32Exception().Message);
                }
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

    private static class NativeMethods
    {
        public const uint ScManagerConnect = 0x0001;
        public const uint ServiceChangeConfig = 0x0002;
        public const uint ServiceConfigPreshutdownInfo = 7;

        [StructLayout(LayoutKind.Sequential)]
        public struct ServicePreshutdownInfo
        {
            public uint Timeout;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr OpenServiceW(IntPtr manager, string serviceName, uint desiredAccess);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref ServicePreshutdownInfo info);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseServiceHandle(IntPtr handle);
    }
}
