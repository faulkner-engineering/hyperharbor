using System.Text.Json.Serialization;

namespace HyperHarbor.Shared.Contracts.Unattend;

/// <summary>The operating system an unattended profile installs. Schema: InstallOs.</summary>
public enum InstallOs
{
    Windows,
    Linux,
}

/// <summary>Windows Setup choices. Schema: WindowsInstallSettings.</summary>
/// <param name="DefaultEdition">The image to install, by its name in install.wim, for example "Windows 11 Pro".</param>
/// <param name="BypassHardwareChecks">Skip Windows 11's TPM, Secure Boot, and memory checks (for VMs without a vTPM).</param>
/// <param name="DisableConsumerFeatures">Turn off suggested apps and other consumer experiences.</param>
public sealed record WindowsInstallSettings(
    [property: JsonRequired] string DefaultEdition,
    bool BypassHardwareChecks = false,
    bool DisableTelemetry = true,
    bool DisableAdvertisingId = true,
    bool DisableLocation = true,
    bool DisableConsumerFeatures = true);

/// <summary>Ubuntu autoinstall choices. Schema: LinuxInstallSettings.</summary>
/// <param name="SshAuthorizedKeys">OpenSSH public keys for the administrator account.</param>
/// <param name="Packages">Extra apt packages to install.</param>
/// <param name="InstallDesktop">Install Xfce and xrdp during setup, so Remote Desktop works.</param>
public sealed record LinuxInstallSettings(
    IReadOnlyList<string>? SshAuthorizedKeys = null,
    IReadOnlyList<string>? Packages = null,
    bool InstallDesktop = true);

/// <summary>Creates or replaces an unattended profile. Schema: UnattendProfileRequest.</summary>
/// <param name="AdminAccountName">The local administrator Setup creates. The host gives it a one-time password and rotates it after setup.</param>
/// <param name="TimeZone">A Windows time zone ID for Windows, an IANA ID for Linux. Null: the host's time zone.</param>
/// <param name="Locale">A language tag, for example "en-US".</param>
/// <param name="Windows">Required when <paramref name="Os"/> is Windows.</param>
/// <param name="Linux">Required when <paramref name="Os"/> is Linux.</param>
public sealed record UnattendProfileRequest(
    [property: JsonRequired] string Name,
    [property: JsonRequired] InstallOs Os,
    string AdminAccountName = UnattendProfile.DefaultAdminAccountName,
    string? TimeZone = null,
    string Locale = UnattendProfile.DefaultLocale,
    WindowsInstallSettings? Windows = null,
    LinuxInstallSettings? Linux = null);

/// <summary>
/// Settings for installing an OS without anyone at the console. Passwords are never part of a profile;
/// the host generates a one-time password for each VM. Schema: UnattendProfile.
/// </summary>
/// <param name="Id">A slug for built-in profiles, a GUID for profiles a User created.</param>
/// <param name="BuiltIn">Built-in profiles cannot be changed or deleted; copy one to customize it.</param>
public sealed record UnattendProfile(
    string Id,
    string Name,
    bool BuiltIn,
    InstallOs Os,
    string AdminAccountName,
    string TimeZone,
    string Locale,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] WindowsInstallSettings? Windows,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] LinuxInstallSettings? Linux)
{
    public const string DefaultAdminAccountName = "hhadmin";
    public const string DefaultLocale = "en-US";
}

/// <summary>What an ISO in the library installs, read from its files. Schema: IsoInspection.</summary>
/// <param name="Os">Null when the image is neither Windows Setup media nor an Ubuntu installer.</param>
/// <param name="Distribution">For example "Windows" or "Ubuntu-Server 24.04.1 LTS"; null when unknown.</param>
/// <param name="Editions">Windows image names in install.wim, for example "Windows 11 Pro"; empty for Linux.</param>
public sealed record IsoInspection(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] InstallOs? Os,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Distribution,
    IReadOnlyList<string> Editions);

/// <summary>Installs the OS without anyone at the console when a VM is created. Schema: UnattendedInstallRequest.</summary>
/// <param name="ProfileId">An ID from GET /unattend-profiles.</param>
/// <param name="WindowsEdition">An edition from the ISO's inspection. Null: the profile's default edition.</param>
/// <param name="ComputerName">Windows only, at most 15 letters, digits, or hyphens. Null: derived from the VM name.</param>
public sealed record UnattendedInstallRequest(
    [property: JsonRequired] string ProfileId,
    string? WindowsEdition = null,
    string? ComputerName = null);

/// <summary>Where an unattended install is. Schema: UnattendedInstallState.</summary>
public enum UnattendedInstallState
{
    /// <summary>Setup is running; the guest has not reported a heartbeat yet.</summary>
    Installing,

    /// <summary>Ubuntu waits for "yes" at the console before it changes the disk.</summary>
    AwaitingConfirmation,

    /// <summary>The guest OS runs but has not reported an address yet.</summary>
    WaitingForGuest,

    /// <summary>The guest has an address; Remote Desktop (Windows) or SSH (Linux) does not answer yet.</summary>
    WaitingForRemoteAccess,

    /// <summary>The host is setting up the User's account.</summary>
    Configuring,

    Ready,
    Failed,
    Canceled,
}

/// <summary>An unattended install and how far it is. Schema: UnattendedInstallStatus.</summary>
/// <param name="Step">What is happening now, for example "Waiting for Remote Desktop to answer".</param>
/// <param name="Error">Why the install failed; null otherwise.</param>
public sealed record UnattendedInstallStatus(
    Guid VmId,
    string ProfileId,
    InstallOs Os,
    UnattendedInstallState State,
    string Step,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Error);
