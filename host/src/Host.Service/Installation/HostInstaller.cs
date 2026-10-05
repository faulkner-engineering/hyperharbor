using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Service.VmConsole;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Win32;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The elevated half of install and uninstall. Copies this executable into the layout, registers the
/// service against the current junction, and sets up the firewall rule, console account, data directory
/// ACL, Start menu shortcut, and Installed apps entry. Progress lines go to <paramref name="progress"/>.
/// </summary>
internal sealed class HostInstaller(InstallLayout layout, IProgress<string> progress)
{
    public const int DefaultPort = 48443;
    private const string Description = "Manages Hyper-V VMs for paired HyperHarbor clients and connects them in one click.";
    private static readonly string[] HostProcessNames = ["HyperHarbor.Host", "HyperHarbor.Host.Service", "HyperHarbor.Host.Tray"];
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(60);

    private static string DataDirectory => HostIdentityStore.DefaultDataDirectory;

    /// <param name="parentProcessId">The unelevated installer waiting for this one; it is not stopped.</param>
    public async Task InstallAsync(SemanticVersion version, int port, SecurityIdentifier trayUser, int? parentProcessId)
    {
        progress.Report("Stopping the running host");
        StopService();
        StopHostProcesses(parentProcessId);

        var previous = layout.CurrentVersion;
        progress.Report($"Copying HyperHarbor {version} to {layout.VersionFolder(version)}");
        layout.Stage(Environment.ProcessPath!, version);
        layout.Activate(version);
        foreach (var pruned in layout.PruneExcept(version, previous))
        {
            progress.Report($"Removed version {pruned}");
        }

        progress.Report("Securing the data folder");
        foreach (var removed in DataDirectoryAcl.Secure(DataDirectory, trayUser))
        {
            progress.Report($"Removed {removed}, which an untrusted account created");
        }

        progress.Report($"Registering the {HostService.Name} service");
        var binaryPath = $"\"{layout.CurrentExecutable}\"" + (port == DefaultPort ? string.Empty : $" --Api:Port={port}");
        ServiceRegistration.CreateOrUpdate(HostService.Name, HostService.DisplayName, Description, binaryPath);
        using (var parameters = Registry.LocalMachine.CreateSubKey(HostService.ParametersKey))
        {
            parameters.SetValue(HostService.TrayUserSidValue, trayUser.Value);
        }

        UninstallEntry.Write(layout, version);
        StartMenuShortcut.Create(layout);

        if (!FirewallRule.Exists())
        {
            progress.Report($"Allowing TCP {port} through the firewall on Private networks");
            FirewallRule.Add(port);
        }

        if (!File.Exists(Path.Combine(DataDirectory, ConsoleSetupHelper.AccountsFileName)))
        {
            progress.Report("Creating the console account");
            var (succeeded, message) = await RunConsoleSetupAsync(ConsoleSetupHelper.SetupSwitch);
            progress.Report(succeeded ? message : $"Console access was not set up: {message} Use Set up console access in the HyperHarbor Host window.");
        }

        progress.Report("Starting the service");
        using (var service = new ServiceController(HostService.Name))
        {
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
        }

        if (!await WaitForPortAsync(port, ListenTimeout))
        {
            throw new InvalidOperationException($"The service is running but does not answer on TCP {port}. See {Path.Combine(DataDirectory, "logs")}.");
        }

        progress.Report($"The service is running on port {port}");
    }

    /// <param name="removeData">Also delete %ProgramData%\HyperHarbor: pairings, settings, and the audit trail.</param>
    public async Task UninstallAsync(bool removeData, int? parentProcessId)
    {
        SecurityIdentifier? trayUser = null;
        using (var parameters = Registry.LocalMachine.OpenSubKey(HostService.ParametersKey))
        {
            if (parameters?.GetValue(HostService.TrayUserSidValue) is string sid)
            {
                trayUser = new SecurityIdentifier(sid);
            }
        }

        progress.Report($"Stopping and removing the {HostService.Name} service");
        StopService();
        ServiceRegistration.Delete(HostService.Name);
        StopHostProcesses(parentProcessId);

        if (File.Exists(Path.Combine(DataDirectory, ConsoleSetupHelper.AccountsFileName)))
        {
            progress.Report("Removing the console account");
            var (_, message) = await RunConsoleSetupAsync(ConsoleSetupHelper.RemoveSwitch);
            progress.Report(message);
        }

        FirewallRule.Delete();
        UninstallEntry.Delete();
        StartMenuShortcut.Delete();
        if (trayUser is not null)
        {
            TrayStartup.Unregister(trayUser);
        }

        if (Directory.Exists(layout.Root))
        {
            progress.Report($"Deleting {layout.Root}");
            foreach (var link in new[] { layout.Current, layout.Current + ".next" }.Where(Junction.IsJunction))
            {
                Junction.Delete(link);
            }

            Directory.Delete(layout.Root, recursive: true);
        }

        if (removeData && Directory.Exists(DataDirectory))
        {
            progress.Report($"Deleting {DataDirectory}");
            Directory.Delete(DataDirectory, recursive: true);
        }

        // Uninstall runs from a temporary copy of the executable (the installed one cannot delete itself).
        if (Environment.ProcessPath is { } self && self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase))
        {
            NativeMethods.MoveFileEx(self, null, NativeMethods.MoveFileDelayUntilReboot);
        }
    }

    private static void StopService()
    {
        if (!ServiceRegistration.Exists(HostService.Name))
        {
            return;
        }

        using var service = new ServiceController(HostService.Name);
        if (service.Status is ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (service.Status is not ServiceControllerStatus.StopPending)
        {
            service.Stop();
        }

        service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
    }

    /// <summary>Stops trays and portable hosts, which lock the executables and hold the port and the pipe.</summary>
    private void StopHostProcesses(int? parentProcessId)
    {
        foreach (var process in HostProcessNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId || process.Id == parentProcessId)
                {
                    continue;
                }

                try
                {
                    process.Kill();
                    process.WaitForExit(TimeSpan.FromSeconds(10));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    progress.Report($"Could not stop {process.ProcessName} ({process.Id}): {ex.Message}");
                }
            }
        }
    }

    /// <summary>Runs the console account helper in this (elevated) process.</summary>
    private static async Task<(bool Succeeded, string Message)> RunConsoleSetupAsync(string command)
    {
        var resultFile = Path.Combine(Path.GetTempPath(), $"hyperharbor-console-{Guid.NewGuid():N}.txt");
        try
        {
            var exitCode = await ConsoleSetupCommand.RunAsync([command, DataDirectory, resultFile]);
            var message = File.Exists(resultFile) ? (await File.ReadAllTextAsync(resultFile)).Trim() : string.Empty;
            return (exitCode == ConsoleSetupCommand.ExitSucceeded, message);
        }
        finally
        {
            File.Delete(resultFile);
        }
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!deadline.IsCancellationRequested)
        {
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, deadline.Token);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None);
            }
        }

        return false;
    }

    private static class NativeMethods
    {
        public const uint MoveFileDelayUntilReboot = 0x4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveFileEx(string existingFileName, string? newFileName, uint flags);
    }
}
