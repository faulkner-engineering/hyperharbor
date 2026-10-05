using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// HyperHarbor.Host.exe started from Explorer without arguments: installs the host if it is missing,
/// offers to update an older installed version, and otherwise opens the tray.
/// </summary>
internal static class Launcher
{
    public static async Task<int> RunAsync()
    {
        var ui = new DialogInstallUi();
        var installed = UninstallEntry.FindInstalled();
        var installedVersion = installed?.CurrentVersion;
        var version = HostVersion.Current;

        if (installed is null || installedVersion is null || version > installedVersion)
        {
            return await InstallCommand.InstallAsync([], ui);
        }

        if (version < installedVersion && !installed.Contains(Environment.ProcessPath!))
        {
            ui.Report(
                $"HyperHarbor {installedVersion} is already installed",
                $"This file is version {version}, which is older. The installed version keeps running; the tray opens now.",
                succeeded: true);
        }

        TrayStartup.Start(installed);
        return InstallCommand.ExitSucceeded;
    }
}
