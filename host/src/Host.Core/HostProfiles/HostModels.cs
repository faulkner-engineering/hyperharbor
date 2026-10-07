using HyperHarbor.Host.Core.Profiles;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>A Windows service as the host reports it. Startup is disabled, manual, automatic, automaticDelayed, boot, or system.</summary>
public sealed record ServiceState(string Name, string DisplayName, string Startup, bool Running);

/// <summary>
/// A startup entry: a value in a Run key or a file in a Startup folder. Scope is "machine" or "user:&lt;sid&gt;"; Source is
/// Run, Run32 (the 32-bit Run key), or Folder. Whether it is enabled comes from StartupApproved, which Task Manager also uses.
/// </summary>
public sealed record StartupEntry(string Scope, string Source, string Name, string Command, bool Enabled)
{
    public string Key => $"{Scope}|{Source}|{Name}";
}

public sealed record PowerPlanInfo(string Guid, string Name);

/// <param name="ActivePlan">The active plan's GUID, lower case.</param>
/// <param name="ArmedDevices">Devices that may wake the PC now, by the name powercfg gives them.</param>
/// <param name="NetworkDevices">Physical wired network adapters whose wake setting powercfg can change.</param>
public sealed record PowerState(
    string ActivePlan,
    IReadOnlyList<PowerPlanInfo> Plans,
    IReadOnlyList<string> ArmedDevices,
    IReadOnlyList<string> NetworkDevices);

/// <param name="Scope">machine, or user:&lt;sid&gt; for a per-user install.</param>
/// <param name="Key">The subkey name below Uninstall.</param>
/// <param name="WindowsInstaller">An MSI package, uninstalled with msiexec /x and its product code (the key name).</param>
public sealed record InstalledProgram(
    string Name,
    string Scope,
    string Key,
    string? UninstallString,
    string? QuietUninstallString,
    bool WindowsInstaller);

public sealed record PrinterInfo(string Name, string Port);

/// <summary>One registry value to read; Users means every signed-in user's hive and the Default user hive.</summary>
public sealed record RegistryProbe(RegistryTarget Target, string Key, string Name)
{
    public string Id => $"{Target}|{Key}|{Name}".ToLowerInvariant();
}

/// <summary>A value found in one hive. Type is dword, qword, string, or other (a kind HyperHarbor does not write); null means the value is absent.</summary>
public sealed record RegistryValueState(string Hive, string? Type, string? Value);

/// <summary>Everything the Lean host action reads from the host, in one pass.</summary>
/// <param name="Registry">By <see cref="RegistryProbe.Id"/>; for users the signed-in hives come first, the Default user hive last.</param>
/// <param name="Warnings">Things that could not be read (a hive that would not load); they become problems in the dry run.</param>
public sealed record HostSnapshot(
    IReadOnlyList<ServiceState> Services,
    IReadOnlyList<StartupEntry> Startup,
    PowerState Power,
    IReadOnlySet<string> Appx,
    IReadOnlyList<InstalledProgram> Programs,
    IReadOnlyList<PrinterInfo> Printers,
    IReadOnlyDictionary<string, IReadOnlyList<RegistryValueState>> Registry,
    IReadOnlyList<string> Warnings);

/// <summary>A game found in a Steam library folder.</summary>
public sealed record SteamGame(long AppId, string Name);

/// <summary>Finds installed Steam games by reading the library folders. Faked in tests.</summary>
public interface ISteamLibrary
{
    IReadOnlyList<SteamGame> InstalledGames();
}

/// <summary>Reads and changes the host. The real one runs Windows PowerShell scripts; tests use an in-memory host.</summary>
public interface IHostSystem
{
    /// <summary>False when the host process lacks administrator rights, so nothing could be changed.</summary>
    bool CanModify { get; }

    Task<HostSnapshot> InspectAsync(IReadOnlyList<RegistryProbe> probes, CancellationToken cancellationToken);

    /// <summary>Makes the changes. One item failing never stops the others.</summary>
    Task<IReadOnlyList<ApplyItemResult>> ApplyAsync(HostDiff diff, CancellationToken cancellationToken);

    /// <summary>Makes a system restore point; throws <see cref="HostLeanException"/> when Windows does not make one.</summary>
    Task<string> CreateRestorePointAsync(string description, CancellationToken cancellationToken);

    /// <summary>Exports registry keys (HKLM\... or HKU\&lt;sid&gt;\...) to .reg files in a folder; keys that do not exist are skipped. Returns the files written.</summary>
    Task<IReadOnlyList<string>> ExportRegistryAsync(IReadOnlyList<string> keys, string folder, CancellationToken cancellationToken);
}

/// <summary>Something the Lean host action cannot do, with text for the person at the PC.</summary>
public class HostLeanException(string message) : Exception(message);

/// <summary>An apply was asked for without a recent dry run of the same profile on the same host state.</summary>
public sealed class DryRunRequiredException(string message) : HostLeanException(message);

/// <summary>One of the two Lean host profiles has the wrong shape or the host cannot run it.</summary>
public sealed class HostLeanUnsupportedException(string message) : HostLeanException(message);
