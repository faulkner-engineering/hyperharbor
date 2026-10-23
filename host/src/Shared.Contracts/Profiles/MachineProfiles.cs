using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts.Backup;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Shared.Contracts.Profiles;

/// <summary>
/// A whole VM in one document: account, display, firmware, compute, GPU share, network, backups, and software.
/// Every section is optional; docs/machine-profiles.md describes how each applies. Schema: MachineProfile.
/// </summary>
/// <param name="Install">As in a setup profile.</param>
public sealed record MachineProfile(
    [property: JsonRequired] string Name,
    [property: JsonRequired] InstallOs Os,
    string? Description = null,
    MachineAccount? Account = null,
    MachineDisplay? Display = null,
    MachineFirmware? Firmware = null,
    MachineCompute? Compute = null,
    MachineGpu? Gpu = null,
    MachineNetwork? Network = null,
    MachineBackup? Backup = null,
    IReadOnlyList<ProfileItem>? Install = null,
    ProfileRemove? Remove = null,
    IReadOnlyList<ProfileTweak>? Tweaks = null,
    ProfileBrowser? Browser = null);

/// <summary>The unattended install settings, checked as an unattended install profile is. Schema: MachineAccount.</summary>
/// <param name="TimeZone">A Windows time zone ID for Windows, an IANA ID for Linux. Null: the host's time zone.</param>
/// <param name="ComputerName">Windows only, at most 15 letters, digits, or hyphens. Null: derived from the VM name.</param>
public sealed record MachineAccount(
    string? AdminAccountName = null,
    string? TimeZone = null,
    string? Locale = null,
    string? ComputerName = null,
    WindowsInstallSettings? Windows = null,
    LinuxInstallSettings? Linux = null);

/// <summary>Schema: MachineDisplay.</summary>
public sealed record MachineDisplay(MachineGuestDisplay? Guest = null, MachineConnection? Connection = null);

/// <summary>Schema: GuestTheme.</summary>
public enum GuestTheme
{
    Dark,
    Light,
}

/// <summary>Settings in the User's account in the VM. Null leaves each as installed. Schema: MachineGuestDisplay.</summary>
/// <param name="AccentColor">#RRGGBB.</param>
public sealed record MachineGuestDisplay(GuestTheme? Theme = null, string? AccentColor = null, MachineWallpaper? Wallpaper = null);

/// <summary>A solid color (#RRGGBB) or an image file the host keeps beside the profile, not both. Schema: MachineWallpaper.</summary>
/// <param name="Image">A plain file name.</param>
public sealed record MachineWallpaper(string? Color = null, string? Image = null);

/// <summary>Schema: ConnectionMonitors.</summary>
public enum ConnectionMonitors
{
    One,
    All,
}

/// <summary>Schema: ConnectionWindow.</summary>
public enum ConnectionWindow
{
    FullScreen,
    Windowed,
}

/// <summary>Remote Desktop defaults for VMs made from the profile; a device's own choice wins. Schema: MachineConnection.</summary>
public sealed record MachineConnection(ConnectionMonitors? Monitors = null, ConnectionWindow? Window = null, MachineRedirect? Redirect = null);

/// <summary>Null leaves a choice to Remote Desktop's default. Schema: MachineRedirect.</summary>
public sealed record MachineRedirect(bool? Clipboard = null, bool? Printers = null, bool? Microphone = null, bool? Camera = null);

/// <summary>Schema: MachineFirmware.</summary>
/// <param name="Tpm">Applying to an existing VM can add a TPM, never remove one.</param>
/// <param name="SecureBoot">The template follows the OS.</param>
public sealed record MachineFirmware(bool? Tpm = null, bool? SecureBoot = null);

/// <summary>Schema: MachineMemory.</summary>
/// <param name="MaximumMb">With dynamic memory; at least <paramref name="StartupMb"/>.</param>
public sealed record MachineMemory([property: JsonRequired] long StartupMb, long? MaximumMb = null, bool Dynamic = true);

/// <summary>Schema: MachineCompute.</summary>
/// <param name="DiskSizeGb">Used when creating a VM; on an existing VM only a larger size is offered.</param>
public sealed record MachineCompute(int? Processors = null, MachineMemory? Memory = null, bool? NestedVirtualization = null, int? DiskSizeGb = null);

/// <summary>Each percent sets the partition's minimum, maximum, and optimal values. Schema: MachineGpuShare.</summary>
public sealed record MachineGpuShare(int VramPercent = 50, int EncodePercent = 50, int DecodePercent = 50, int ComputePercent = 50);

/// <summary>Performance mode; Windows only, and memory must be fixed. Schema: MachineGpu.</summary>
/// <param name="Adapter">A GPU name to match on the host. Null: the host's first partitionable GPU.</param>
public sealed record MachineGpu([property: JsonRequired] bool Enabled, string? Adapter = null, MachineGpuShare? Share = null, bool? HardwareEncoding = null);

/// <summary>Schema: MachineNetwork.</summary>
/// <param name="Switch">A virtual switch name. Null: the host's default switch.</param>
public sealed record MachineNetwork(string? Switch = null, bool? MacAddressSpoofing = null);

/// <summary>A backup policy; the schedule belongs to the named target. Schema: MachineBackup.</summary>
/// <param name="Target">A backup target's name on the host.</param>
/// <param name="Retention">Null: the target's retention.</param>
/// <param name="Conditions">Null: the target's conditions.</param>
public sealed record MachineBackup(
    [property: JsonRequired] bool Enabled,
    string? Target = null,
    BackupTriggers? Triggers = null,
    BackupRetention? Retention = null,
    BackupConditions? Conditions = null,
    BackupGpuMode? GpuMode = null);

/// <summary>Creates a VM from a machine profile. Schema: CreateVmFromProfileRequest.</summary>
/// <param name="IsoName">An image in the ISO library.</param>
/// <param name="WindowsEdition">An edition from the ISO's inspection. Null: the profile's edition.</param>
public sealed record CreateVmFromProfileRequest(
    [property: JsonRequired] string Name,
    [property: JsonRequired] string IsoName,
    string? WindowsEdition = null,
    bool AcknowledgeWarnings = false);

/// <summary>A saved machine profile. Schema: StoredMachineProfile.</summary>
public sealed record StoredMachineProfile(string Id, DateTimeOffset UpdatedAt, MachineProfile Profile);

/// <summary>A machine profile in the list. Schema: MachineProfileSummary.</summary>
/// <param name="SoftwareCount">Packages, removals, tweaks, and extensions together.</param>
/// <param name="Error">Why the file on the host does not read; the other fields are then empty.</param>
public sealed record MachineProfileSummary(
    string Id,
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    InstallOs Os,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Processors,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? MemoryMb,
    bool Gpu,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] bool? Tpm,
    bool Backup,
    int SoftwareCount,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);
