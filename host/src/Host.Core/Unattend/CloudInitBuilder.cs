using System.Text;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>What one Ubuntu install needs besides its profile.</summary>
/// <param name="HostName">See <see cref="CloudInitBuilder.HostName"/>.</param>
/// <param name="AdminPasswordHash">SHA-512 crypt of the one-time password; the password itself never goes into the seed.</param>
public sealed record LinuxInstall(UnattendProfile Profile, string HostName, string AdminPasswordHash)
{
    public override string ToString() => $"LinuxInstall {{ Profile = {Profile.Id}, HostName = {HostName} }}";
}

/// <summary>
/// Builds the NoCloud seed (user-data and meta-data, on a volume labeled CIDATA) for Ubuntu's
/// autoinstall. The installer asks "Continue with autoinstall?" at the console before it starts,
/// because the seed cannot add "autoinstall" to the kernel command line.
/// Every value written here was validated by <see cref="UnattendProfileStore"/>, and all of them are
/// single-quoted YAML scalars, so none can add keys.
/// </summary>
public static class CloudInitBuilder
{
    public const string VolumeLabel = "CIDATA";
    public const string UserDataFileName = "user-data";
    public const string MetaDataFileName = "meta-data";

    /// <summary>Lets Hyper-V read the guest's addresses (hv_kvp_daemon), which readiness detection needs.</summary>
    public const string HyperVToolsPackage = "linux-cloud-tools-virtual";

    /// <summary>The desktop the existing Linux setup installs, so Remote Desktop (xrdp) has a session.</summary>
    private static readonly string[] DesktopPackages = ["xfce4", "xfce4-goodies", "xrdp"];

    public static string UserData(LinuxInstall install)
    {
        ArgumentNullException.ThrowIfNull(install);
        var profile = install.Profile;
        var settings = profile.Linux ?? throw new ArgumentException("The profile is not a Linux profile.", nameof(install));
        if (!install.AdminPasswordHash.StartsWith("$6$", StringComparison.Ordinal))
        {
            throw new ArgumentException("The administrator password must be a SHA-512 crypt hash.", nameof(install));
        }

        string[] packages = [HyperVToolsPackage, .. settings.InstallDesktop ? DesktopPackages : [], .. settings.Packages ?? []];
        var keys = settings.SshAuthorizedKeys ?? [];

        var yaml = new StringBuilder()
            .AppendLine("#cloud-config")
            .AppendLine("autoinstall:")
            .AppendLine("  version: 1")
            .AppendLine($"  locale: {Quote(profile.Locale.Replace('-', '_') + ".UTF-8")}")
            .AppendLine($"  timezone: {Quote(profile.TimeZone)}")
            .AppendLine("  identity:")
            .AppendLine($"    hostname: {Quote(install.HostName)}")
            .AppendLine($"    username: {Quote(profile.AdminAccountName)}")
            .AppendLine($"    realname: {Quote("HyperHarbor administrator")}")
            .AppendLine($"    password: {Quote(install.AdminPasswordHash)}")
            .AppendLine("  ssh:")
            .AppendLine("    install-server: true")
            // The host signs in with the password to finish setup, so password sign-in stays on.
            .AppendLine("    allow-pw: true");
        if (keys.Count > 0)
        {
            yaml.AppendLine("    authorized-keys:");
            foreach (var key in keys)
            {
                yaml.AppendLine($"      - {Quote(key)}");
            }
        }

        yaml.AppendLine("  packages:");
        foreach (var package in packages.Distinct())
        {
            yaml.AppendLine($"    - {Quote(package)}");
        }

        return yaml.ToString().ReplaceLineEndings("\n");
    }

    public static string MetaData(LinuxInstall install) =>
        $"instance-id: {Quote("hyperharbor-" + install.HostName)}\nlocal-hostname: {Quote(install.HostName)}\n";

    /// <summary>A Linux host name from the VM name: lowercase letters, digits, and hyphens, at most 63 characters.</summary>
    public static string HostName(string vmName)
    {
        var cleaned = new StringBuilder();
        foreach (var c in vmName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                cleaned.Append(c);
            }
            else if (cleaned.Length > 0 && cleaned[^1] != '-')
            {
                cleaned.Append('-');
            }
        }

        var name = cleaned.ToString();
        name = (name.Length > 63 ? name[..63] : name).Trim('-');
        return name.Length == 0 ? "hyperharbor-vm" : name;
    }

    /// <summary>A single-quoted YAML scalar. Line breaks are refused rather than escaped.</summary>
    private static string Quote(string value)
    {
        if (value.Any(char.IsControl))
        {
            throw new ArgumentException("A seed value contains a control character.", nameof(value));
        }

        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
