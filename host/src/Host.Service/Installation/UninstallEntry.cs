using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Win32;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>The "Installed apps" entry in Settings, which also records where the host is installed.</summary>
internal static class UninstallEntry
{
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + HostService.Name;

    public static void Write(InstallLayout layout, SemanticVersion version)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        var executable = $"\"{layout.CurrentExecutable}\"";
        key.SetValue("DisplayName", HostService.DisplayName);
        key.SetValue("DisplayVersion", version.ToString());
        key.SetValue("Publisher", "HyperHarbor");
        key.SetValue("InstallLocation", layout.Root);
        key.SetValue("DisplayIcon", layout.CurrentExecutable + ",0");
        key.SetValue("UninstallString", $"{executable} {HostCommandLine.UninstallVerb}");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)(new FileInfo(layout.CurrentExecutable).Length / 1024), RegistryValueKind.DWord);
    }

    public static void Delete() => Registry.LocalMachine.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);

    /// <summary>The installed host's folders, or null when the service is not installed.</summary>
    public static InstallLayout? FindInstalled()
    {
        if (!ServiceRegistration.Exists(HostService.Name))
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey(Key);
        return new InstallLayout(key?.GetValue("InstallLocation") as string is { Length: > 0 } root ? root : InstallLayout.DefaultRoot);
    }
}
