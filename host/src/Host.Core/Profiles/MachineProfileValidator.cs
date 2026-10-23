using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Backup;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Checks a machine profile and tidies it. The account section follows the unattended install profile rules and the
/// software sections the setup profile rules, so a profile means the same thing whichever way it is made. Names of
/// things on a host (switch, GPU, backup target) are checked when the profile is applied, not here.
/// </summary>
public sealed partial class MachineProfileValidator(ProfileValidator software, UnattendProfileStore unattend)
{
    /// <summary>Hyper-V's limit for a generation 2 VM.</summary>
    public const int MaxProcessors = 240;
    public const long MinMemoryMb = 512;
    public const int MaxDiskSizeGb = 65536;
    public const int MaxNameLength = 100;

    /// <summary>The edition an account section without Windows settings installs.</summary>
    public const string DefaultWindowsEdition = "Windows 11 Pro";

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex Color();

    [GeneratedRegex("^[A-Za-z0-9-]{1,15}$")]
    private static partial Regex ComputerName();

    [GeneratedRegex(@"^[^\\/:*?""<>|]{1,100}\.(jpg|jpeg|png|bmp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImageFile();

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$")]
    private static partial Regex TimeOfDay();

    public IReadOnlyDictionary<string, string> PolicyTypes => software.PolicyTypes;

    /// <exception cref="LifecycleValidationException">The profile has problems; Errors lists each by field path.</exception>
    public MachineProfile Normalize(MachineProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var issues = new List<ValidationIssue>();
        var windows = profile.Os == InstallOs.Windows;

        var account = profile.Account is null ? null : Account(profile, issues);
        var display = profile.Display is null ? null : Display(profile.Display, issues);
        var compute = profile.Compute is null ? null : Compute(profile.Compute, issues);
        var gpu = profile.Gpu;
        if (gpu is not null)
        {
            if (gpu.Enabled && !windows)
            {
                issues.Add(new("gpu.enabled", "Performance mode is for Windows VMs."));
            }

            if (gpu.Enabled && compute?.Memory is { Dynamic: true })
            {
                issues.Add(new("compute.memory.dynamic", "Performance mode needs fixed memory. Turn dynamic memory off."));
            }

            if (gpu.Share is { } share && new[] { share.VramPercent, share.EncodePercent, share.DecodePercent, share.ComputePercent }.Any(percent => percent is < 1 or > 100))
            {
                issues.Add(new("gpu.share", "Each share is 1 to 100 percent."));
            }

            gpu = gpu with { Adapter = Name(gpu.Adapter, "gpu.adapter", issues) };
        }

        if (profile.Firmware is { Tpm: false } && account?.Windows is { BypassHardwareChecks: false } settings
            && settings.DefaultEdition.StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("firmware.tpm", "Windows 11 Setup needs a TPM. Turn the TPM on, or bypass the hardware checks in Account."));
        }

        var network = profile.Network is null ? null : profile.Network with { Switch = Name(profile.Network.Switch, "network.switch", issues) };
        var backup = profile.Backup is null ? null : Backup(profile.Backup, issues);

        var hasSoftware = profile.Install is not null || profile.Remove is not null || profile.Tweaks is not null || profile.Browser is not null;
        if (hasSoftware && !windows)
        {
            issues.Add(new("install", "Packages, removals, tweaks, and browsers are for Windows VMs. For Linux, list apt packages in Account."));
        }

        var name = profile.Name?.Trim() ?? "";
        var description = string.IsNullOrWhiteSpace(profile.Description) ? null : profile.Description.Trim();
        SetupProfile tidy;
        try
        {
            tidy = software.Normalize(MachineProfileYaml.Software(profile with { Name = name, Description = description }));
        }
        catch (LifecycleValidationException ex)
        {
            issues.AddRange(ex.Errors);
            tidy = MachineProfileYaml.Software(profile);
        }

        if (issues.Count > 0)
        {
            throw new LifecycleValidationException(issues[0].Message, issues);
        }

        return profile with
        {
            Name = tidy.Name,
            Description = tidy.Description,
            Account = account,
            Display = display,
            Compute = compute,
            Gpu = gpu,
            Network = network,
            Backup = backup,
            Install = tidy.Install,
            Remove = tidy.Remove,
            Tweaks = tidy.Tweaks,
            Browser = tidy.Browser,
        };
    }

    private MachineAccount Account(MachineProfile profile, List<ValidationIssue> issues)
    {
        var account = profile.Account!;
        var windows = profile.Os == InstallOs.Windows;
        var settings = windows ? account.Windows ?? new WindowsInstallSettings(DefaultWindowsEdition) : account.Windows;

        // Settings for the other OS stay in the request, so the unattended profile rules report them.
        var request = new UnattendProfileRequest(
            "machine profile",
            profile.Os,
            account.AdminAccountName ?? UnattendProfile.DefaultAdminAccountName,
            account.TimeZone,
            account.Locale ?? UnattendProfile.DefaultLocale,
            settings,
            windows ? account.Linux : account.Linux ?? new LinuxInstallSettings());

        UnattendProfile? checkedAccount = null;
        try
        {
            checkedAccount = unattend.Normalize("machine", request);
        }
        catch (LifecycleValidationException ex)
        {
            issues.AddRange(ex.Errors.Select(issue => issue with { Field = "account." + issue.Field }));
        }

        string? computerName = null;
        if (account.ComputerName is { } wanted)
        {
            computerName = wanted.Trim();
            if (!windows)
            {
                issues.Add(new("account.computerName", "The computer name is for Windows; Linux takes its host name from the VM name."));
            }
            else if (!ComputerName().IsMatch(computerName) || computerName.All(char.IsAsciiDigit))
            {
                issues.Add(new("account.computerName", "Use 1 to 15 letters, digits, or hyphens, not only digits."));
            }
        }

        // Keep what the profile says (a null time zone stays "the host's"), tidied by the unattended profile rules.
        return new MachineAccount(
            checkedAccount?.AdminAccountName ?? account.AdminAccountName,
            string.IsNullOrWhiteSpace(account.TimeZone) ? null : account.TimeZone.Trim(),
            checkedAccount?.Locale ?? account.Locale,
            computerName,
            checkedAccount?.Windows ?? (windows ? settings : null),
            checkedAccount?.Linux ?? (windows ? null : account.Linux));
    }

    private static MachineDisplay Display(MachineDisplay display, List<ValidationIssue> issues)
    {
        if (display.Guest is { } guest)
        {
            if (guest.AccentColor is { } accent && !Color().IsMatch(accent))
            {
                issues.Add(new("display.guest.accentColor", "Use a color as #RRGGBB."));
            }

            if (guest.Wallpaper is { } wallpaper)
            {
                if ((wallpaper.Color is null) == (wallpaper.Image is null))
                {
                    issues.Add(new("display.guest.wallpaper", "Choose a color or an image for the wallpaper."));
                }
                else if (wallpaper.Color is { } color && !Color().IsMatch(color))
                {
                    issues.Add(new("display.guest.wallpaper.color", "Use a color as #RRGGBB."));
                }
                else if (wallpaper.Image is { } image && !ImageFile().IsMatch(image))
                {
                    issues.Add(new("display.guest.wallpaper.image", "Use a file name ending in .jpg, .png, or .bmp, without a folder."));
                }
            }
        }

        return display;
    }

    private static MachineCompute Compute(MachineCompute compute, List<ValidationIssue> issues)
    {
        if (compute.Processors is < 1 or > MaxProcessors)
        {
            issues.Add(new("compute.processors", $"Use 1 to {MaxProcessors} virtual processors."));
        }

        var memory = compute.Memory;
        if (memory is not null)
        {
            if (memory.StartupMb < MinMemoryMb || memory.StartupMb % 2 != 0)
            {
                issues.Add(new("compute.memory.startupMb", $"Use at least {MinMemoryMb} MB, an even number."));
            }

            if (memory.Dynamic && memory.MaximumMb is { } maximum && (maximum < memory.StartupMb || maximum % 2 != 0))
            {
                issues.Add(new("compute.memory.maximumMb", "Use an even number at least as large as the startup memory."));
            }

            // Without dynamic memory the maximum is the startup memory, as the compute dialog shows it.
            if (!memory.Dynamic)
            {
                memory = memory with { MaximumMb = null };
            }
        }

        if (compute.DiskSizeGb is < 1 or > MaxDiskSizeGb)
        {
            issues.Add(new("compute.diskSizeGb", $"Use 1 to {MaxDiskSizeGb} GB."));
        }

        return compute with { Memory = memory };
    }

    private static MachineBackup Backup(MachineBackup backup, List<ValidationIssue> issues)
    {
        var target = Name(backup.Target, "backup.target", issues);
        if (backup.Enabled && target is null)
        {
            issues.Add(new("backup.target", "Name the backup target to use, as it is named on the host."));
        }

        if (backup.Triggers is { DebounceMinutes: < 1 or > 1440 })
        {
            issues.Add(new("backup.triggers.debounceMinutes", "Use 1 to 1440 minutes."));
        }

        if (backup.Retention is { } retention && (retention.Daily < 0 || retention.Weekly < 0 || retention.Monthly < 0))
        {
            issues.Add(new("backup.retention", "Keep zero or more of each."));
        }

        if (backup.Conditions?.Window is { } window && (!TimeOfDay().IsMatch(window.Start) || !TimeOfDay().IsMatch(window.End)))
        {
            issues.Add(new("backup.conditions.window", "Use times as HH:mm."));
        }

        return backup with { Target = target };
    }

    /// <summary>A trimmed name of a thing on the host, or null for none.</summary>
    private static string? Name(string? value, string field, List<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var name = value.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            issues.Add(new(field, $"Use a name of at most {MaxNameLength} characters."));
        }

        return name;
    }
}
