using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>What one Windows install needs besides its profile.</summary>
/// <param name="Edition">The image name in install.wim, for example "Windows 11 Pro".</param>
/// <param name="ComputerName">At most 15 characters; see <see cref="AutounattendBuilder.ComputerName"/>.</param>
/// <param name="AdminPassword">The one-time password the host rotates once setup is done.</param>
/// <param name="UserAccountName">The User's VM account, for example hh-owner. Created in the guest with a random password that never leaves it.</param>
public sealed record WindowsInstall(UnattendProfile Profile, string Edition, string ComputerName, string AdminPassword, string UserAccountName)
{
    public override string ToString() => $"WindowsInstall {{ Profile = {Profile.Id}, Edition = {Edition}, ComputerName = {ComputerName} }}";
}

/// <summary>
/// Builds Autounattend.xml for a Generation 2 VM. Windows Setup reads it from the root of the second DVD.
/// It wipes disk 0 into a UEFI layout, installs the chosen edition, skips OOBE, creates the profile's
/// administrator with the one-time password, signs it in once, and runs first logon commands that enable
/// Remote Desktop, create the User's account, apply the privacy settings, and sign out.
/// The only secret in the file is the one-time password, in Windows' obfuscated (not plaintext) form.
/// </summary>
public static class AutounattendBuilder
{
    public const string FileName = "Autounattend.xml";
    public const int MaxComputerNameLength = 15;

    private static readonly XNamespace Ns = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    /// <summary>The Remote Desktop firewall rule group, by its resource name, so it works in every language.</summary>
    private const string RemoteDesktopFirewallGroup = "@FirewallAPI.dll,-28752";

    /// <summary>Remote Desktop Users, by SID, so it works in every language.</summary>
    private const string RemoteDesktopUsersSid = "S-1-5-32-555";

    /// <summary>
    /// Microsoft's published generic installation keys. They select an edition and do not activate it,
    /// so Setup does not stop to ask for a key or an edition.
    /// </summary>
    private static readonly (string Suffix, string Key)[] GenericKeys =
    [
        ("Pro for Workstations", "DXG7C-N36C4-C4HTG-X4T3X-2YV77"),
        ("Pro N", "2B87N-8KFHP-DKV6R-Y2C8J-PKCKT"),
        ("Pro", "VK7JG-NPHTM-C97JM-9MPGT-3V66T"),
        ("Home N", "4CPRK-NM3K3-X6XXQ-RXX86-WXCHW"),
        ("Home", "YTMG3-N6DKC-DKB77-7M9GH-8HVX7"),
        ("Education", "YNMGQ-8RYV3-4PGQ3-C8XTP-7CFBY"),
        ("Enterprise", "XGVPP-NMH47-7TTHJ-W3FW7-8HV2C"),
    ];

    public static string Build(WindowsInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        var profile = install.Profile;
        var settings = profile.Windows ?? throw new ArgumentException("The profile is not a Windows profile.", nameof(install));
        if (!install.UserAccountName.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || install.UserAccountName.Length is 0 or > 20)
        {
            // It is embedded in a PowerShell command; VmAccountName only produces these characters.
            throw new ArgumentException("The VM account name must be letters, digits, and hyphens.", nameof(install));
        }


        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(Ns + "unattend",
                new XAttribute(XNamespace.Xmlns + "wcm", Wcm),
                Pass("windowsPE",
                    Component("Microsoft-Windows-International-Core-WinPE",
                        new XElement(Ns + "SetupUILanguage", new XElement(Ns + "UILanguage", profile.Locale)),
                        Locales(profile.Locale)),
                    Component("Microsoft-Windows-Setup",
                        settings.BypassHardwareChecks ? HardwareCheckBypass() : null,
                        DiskLayout(),
                        new XElement(Ns + "ImageInstall",
                            new XElement(Ns + "OSImage",
                                new XElement(Ns + "InstallFrom",
                                    new XElement(Ns + "MetaData", Add(),
                                        new XElement(Ns + "Key", "/IMAGE/NAME"),
                                        new XElement(Ns + "Value", install.Edition))),
                                new XElement(Ns + "InstallTo",
                                    new XElement(Ns + "DiskID", 0),
                                    new XElement(Ns + "PartitionID", 3)))),
                        new XElement(Ns + "UserData",
                            new XElement(Ns + "AcceptEula", "true"),
                            new XElement(Ns + "ProductKey",
                                new XElement(Ns + "Key", GenericKey(install.Edition) ?? string.Empty),
                                new XElement(Ns + "WillShowUI", "OnError"))))),
                Pass("specialize",
                    Component("Microsoft-Windows-Shell-Setup",
                        new XElement(Ns + "ComputerName", install.ComputerName),
                        new XElement(Ns + "TimeZone", profile.TimeZone))),
                Pass("oobeSystem",
                    Component("Microsoft-Windows-International-Core", Locales(profile.Locale)),
                    Component("Microsoft-Windows-Shell-Setup",
                        new XElement(Ns + "OOBE",
                            new XElement(Ns + "HideEULAPage", "true"),
                            new XElement(Ns + "HideOEMRegistrationScreen", "true"),
                            new XElement(Ns + "HideOnlineAccountScreens", "true"),
                            new XElement(Ns + "HideWirelessSetupInOOBE", "true"),
                            new XElement(Ns + "ProtectYourPC", 3)),
                        new XElement(Ns + "UserAccounts",
                            new XElement(Ns + "LocalAccounts",
                                new XElement(Ns + "LocalAccount", Add(),
                                    HiddenPassword(install.AdminPassword),
                                    new XElement(Ns + "Group", "Administrators"),
                                    new XElement(Ns + "DisplayName", "HyperHarbor administrator"),
                                    new XElement(Ns + "Name", profile.AdminAccountName)))),
                        new XElement(Ns + "AutoLogon",
                            HiddenPassword(install.AdminPassword),
                            new XElement(Ns + "Enabled", "true"),
                            new XElement(Ns + "LogonCount", 1),
                            new XElement(Ns + "Username", profile.AdminAccountName)),
                        new XElement(Ns + "FirstLogonCommands",
                            FirstLogonCommands(settings, install.UserAccountName)
                                .Select((command, index) => new XElement(Ns + "SynchronousCommand", Add(),
                                    new XElement(Ns + "Order", index + 1),
                                    new XElement(Ns + "CommandLine", command.CommandLine),
                                    new XElement(Ns + "Description", command.Description))))))));

        var output = new StringBuilder();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
        {
            document.Save(writer);
        }

        // StringBuilder output is declared as UTF-16; the file is written as UTF-8.
        return output.ToString().Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// A NetBIOS-safe computer name from the VM name: letters, digits, and hyphens, at most 15 characters,
    /// not all digits. Falls back to "HYPERHARBOR-VM".
    /// </summary>
    public static string ComputerName(string vmName)
    {
        var cleaned = new StringBuilder();
        foreach (var c in vmName)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                cleaned.Append(char.ToUpperInvariant(c));
            }
            else if (cleaned.Length > 0 && cleaned[^1] != '-')
            {
                cleaned.Append('-');
            }
        }

        var name = cleaned.ToString();
        name = (name.Length > MaxComputerNameLength ? name[..MaxComputerNameLength] : name).Trim('-');
        return name.Length == 0 || name.All(char.IsAsciiDigit) ? "HYPERHARBOR-VM" : name;
    }

    /// <summary>The generic installation key for an edition name, or null when it is not a known retail edition.</summary>
    public static string? GenericKey(string edition)
    {
        // "Windows 10 Pro" or "Windows 11 Pro": the edition is everything after the version, matched exactly,
        // so "Pro Education" or "Home Single Language" never get another edition's key.
        var parts = edition.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !parts[0].Equals("Windows", StringComparison.OrdinalIgnoreCase) || parts[1] is not ("10" or "11"))
        {
            return null;
        }

        return GenericKeys.FirstOrDefault(entry => parts[2].Equals(entry.Suffix, StringComparison.OrdinalIgnoreCase)).Key;
    }

    /// <summary>Windows' unattend password form: base64 of UTF-16LE (password + "Password"). Obfuscation, not encryption.</summary>
    public static string HidePassword(string password) =>
        Convert.ToBase64String(Encoding.Unicode.GetBytes(password + "Password"));

    private static XElement HiddenPassword(string password) =>
        new(Ns + "Password",
            new XElement(Ns + "Value", HidePassword(password)),
            new XElement(Ns + "PlainText", "false"));

    private static IEnumerable<(string CommandLine, string Description)> FirstLogonCommands(WindowsInstallSettings settings, string userAccountName)
    {
        yield return (
            @"reg add ""HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server"" /v fDenyTSConnections /t REG_DWORD /d 0 /f",
            "Enable Remote Desktop");
        yield return (
            PowerShell($"Enable-NetFirewallRule -Group '{RemoteDesktopFirewallGroup}'"),
            "Allow Remote Desktop through the firewall");

        // The password is random and never leaves the guest; the host sets a new one on every connect.
        yield return (
            PowerShell(
                "$b = [Security.Cryptography.RandomNumberGenerator]::GetBytes(24); " +
                "$p = ConvertTo-SecureString ([Convert]::ToBase64String($b) + 'a1!') -AsPlainText -Force; " +
                $"if (-not (Get-LocalUser -Name '{userAccountName}' -ErrorAction SilentlyContinue)) {{ " +
                $"New-LocalUser -Name '{userAccountName}' -Password $p -PasswordNeverExpires -AccountNeverExpires | Out-Null }}; " +
                $"Add-LocalGroupMember -SID '{RemoteDesktopUsersSid}' -Member '{userAccountName}' -ErrorAction SilentlyContinue"),
            $"Create {userAccountName} for Remote Desktop");

        foreach (var (key, value, data, enabled) in new[]
        {
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", 0, settings.DisableTelemetry),
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", 1, settings.DisableAdvertisingId),
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", 1, settings.DisableLocation),
            (@"HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1, settings.DisableConsumerFeatures),
        })
        {
            if (enabled)
            {
                yield return (
                    string.Create(CultureInfo.InvariantCulture, $@"reg add ""{key}"" /v {value} /t REG_DWORD /d {data} /f"),
                    $"Privacy: {value}");
            }
        }

        yield return ("shutdown /l", "Sign out the setup session");
    }

    private static string PowerShell(string command) => $"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command \"{command}\"";

    private static XElement HardwareCheckBypass() =>
        new(Ns + "RunSynchronous",
            new[] { "BypassTPMCheck", "BypassSecureBootCheck", "BypassRAMCheck" }.Select((value, index) =>
                new XElement(Ns + "RunSynchronousCommand", Add(),
                    new XElement(Ns + "Order", index + 1),
                    new XElement(Ns + "Path", $@"reg add HKLM\SYSTEM\Setup\LabConfig /v {value} /t REG_DWORD /d 1 /f"))));

    /// <summary>Disk 0 as EFI (100 MB, FAT32), MSR (16 MB), and Windows (the rest, NTFS, C:).</summary>
    private static XElement DiskLayout() =>
        new(Ns + "DiskConfiguration",
            new XElement(Ns + "Disk", Add(),
                new XElement(Ns + "DiskID", 0),
                new XElement(Ns + "WillWipeDisk", "true"),
                new XElement(Ns + "CreatePartitions",
                    CreatePartition(1, "EFI", 100),
                    CreatePartition(2, "MSR", 16),
                    new XElement(Ns + "CreatePartition", Add(),
                        new XElement(Ns + "Order", 3),
                        new XElement(Ns + "Type", "Primary"),
                        new XElement(Ns + "Extend", "true"))),
                new XElement(Ns + "ModifyPartitions",
                    new XElement(Ns + "ModifyPartition", Add(),
                        new XElement(Ns + "Order", 1),
                        new XElement(Ns + "PartitionID", 1),
                        new XElement(Ns + "Label", "System"),
                        new XElement(Ns + "Format", "FAT32")),
                    new XElement(Ns + "ModifyPartition", Add(),
                        new XElement(Ns + "Order", 2),
                        new XElement(Ns + "PartitionID", 2)),
                    new XElement(Ns + "ModifyPartition", Add(),
                        new XElement(Ns + "Order", 3),
                        new XElement(Ns + "PartitionID", 3),
                        new XElement(Ns + "Label", "Windows"),
                        new XElement(Ns + "Letter", "C"),
                        new XElement(Ns + "Format", "NTFS")))));

    private static XElement CreatePartition(int order, string type, int sizeMb) =>
        new(Ns + "CreatePartition", Add(),
            new XElement(Ns + "Order", order),
            new XElement(Ns + "Type", type),
            new XElement(Ns + "Size", sizeMb));

    private static object[] Locales(string locale) =>
    [
        new XElement(Ns + "InputLocale", locale),
        new XElement(Ns + "SystemLocale", locale),
        new XElement(Ns + "UILanguage", locale),
        new XElement(Ns + "UserLocale", locale),
    ];

    private static XElement Pass(string name, params object?[] components) =>
        new(Ns + "settings", new XAttribute("pass", name), components);

    private static XElement Component(string name, params object?[] content) =>
        new(Ns + "component",
            new XAttribute("name", name),
            new XAttribute("processorArchitecture", "amd64"),
            new XAttribute("publicKeyToken", "31bf3856ad364e35"),
            new XAttribute("language", "neutral"),
            new XAttribute("versionScope", "nonSxS"),
            content);

    private static XAttribute Add() => new(Wcm + "action", "add");
}
