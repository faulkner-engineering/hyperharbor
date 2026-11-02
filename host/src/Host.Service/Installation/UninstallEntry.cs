using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Win32;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// The "Installed apps" (Add/Remove Programs) entry in Settings, which also records where the host is installed.
/// winget finds the host by this key's name (the manifest's ProductCode), decides whether an upgrade is needed from
/// DisplayVersion, and removes it with QuietUninstallString, so the entry must follow self-updates too.
/// </summary>
internal static class UninstallEntry
{
    /// <summary>The key name under Uninstall: the ProductCode in the winget manifest (packaging/winget).</summary>
    public const string ProductCode = HostService.Name;
    public const string Publisher = "Faulkner Engineering LLC";
    public const string AboutUrl = "https://faulkner.engineering/hyperharbor";
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductCode;

    /// <summary>The entry's values for <paramref name="version"/> installed in <paramref name="layout"/>.</summary>
    public static IReadOnlyDictionary<string, object> Values(InstallLayout layout, SemanticVersion version)
    {
        var executable = $"\"{layout.CurrentExecutable}\"";
        var values = new Dictionary<string, object>
        {
            ["DisplayName"] = HostService.DisplayName,
            ["DisplayVersion"] = version.ToString(),
            ["Publisher"] = Publisher,
            ["InstallLocation"] = layout.Root,
            ["DisplayIcon"] = layout.CurrentExecutable + ",0",
            ["URLInfoAbout"] = AboutUrl,
            ["UninstallString"] = $"{executable} {HostCommandLine.UninstallVerb}",
            ["QuietUninstallString"] = $"{executable} {HostCommandLine.UninstallVerb} {InstallCommand.QuietSwitch}",
            ["NoModify"] = 1,
            ["NoRepair"] = 1,
        };
        if (EstimatedSize(layout) is { } size)
        {
            values["EstimatedSize"] = size;
        }

        return values;
    }

    public static void Write(InstallLayout layout, SemanticVersion version) => Write(Registry.LocalMachine, KeyPath, layout, version);

    /// <summary>Writes the entry under <paramref name="keyPath"/> of <paramref name="hive"/> (tests use HKCU).</summary>
    internal static void Write(RegistryKey hive, string keyPath, InstallLayout layout, SemanticVersion version)
    {
        using var key = hive.CreateSubKey(keyPath);
        foreach (var (name, value) in Values(layout, version))
        {
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Sets DisplayVersion (and EstimatedSize) to the version current points at, after the update helper has
    /// applied or rolled back an update. Does nothing when the host is not installed.
    /// </summary>
    /// <returns>The version written, or null when there is no entry or no current version.</returns>
    public static SemanticVersion? UpdateVersion(InstallLayout layout) => UpdateVersion(Registry.LocalMachine, KeyPath, layout);

    internal static SemanticVersion? UpdateVersion(RegistryKey hive, string keyPath, InstallLayout layout)
    {
        using var key = hive.OpenSubKey(keyPath, writable: true);
        if (key is null || layout.CurrentVersion is not { } version)
        {
            return null;
        }

        key.SetValue("DisplayVersion", version.ToString());
        if (EstimatedSize(layout) is { } size)
        {
            key.SetValue("EstimatedSize", size, RegistryValueKind.DWord);
        }

        return version;
    }

    public static void Delete() => Registry.LocalMachine.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);

    /// <summary>The installed host's folders, or null when the service is not installed.</summary>
    public static InstallLayout? FindInstalled()
    {
        if (!ServiceRegistration.Exists(HostService.Name))
        {
            return null;
        }

        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return new InstallLayout(key?.GetValue("InstallLocation") as string is { Length: > 0 } root ? root : InstallLayout.DefaultRoot);
    }

    /// <summary>The executable's size in KB, which Settings shows.</summary>
    private static int? EstimatedSize(InstallLayout layout) =>
        File.Exists(layout.CurrentExecutable) ? (int)(new FileInfo(layout.CurrentExecutable).Length / 1024) : null;
}
