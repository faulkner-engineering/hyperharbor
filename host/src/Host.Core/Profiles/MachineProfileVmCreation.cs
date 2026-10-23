using System.Globalization;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>Backup policies for VMs made from machine profiles, which name their target.</summary>
public interface IMachineBackupPolicies
{
    /// <summary>The ID of the backup target with this name (ignoring case), or null.</summary>
    string? FindTarget(string name);

    /// <summary>Sets the VM's policy for the target, with the target's defaults for what the profile leaves out.</summary>
    void Set(Guid vmId, string targetId, MachineBackup backup);
}

/// <summary>
/// Creates a VM from a machine profile (docs/machine-profiles.md). The profile is read against this host first:
/// names of things on the host become IDs, empty sections take the Create VM defaults, the account becomes the
/// unattended install, and the software and guest display settings become the setup profile the install applies when
/// ready. The VM is then created by <see cref="VmCreationService"/> as one job, which also sets nested virtualization,
/// MAC address spoofing, Performance mode, and the backup policy while the VM is still off. Once the install is
/// ready, the watcher starts the guest part of Performance mode.
/// </summary>
public sealed class MachineProfileVmCreation(
    MachineProfileStore profiles,
    VmCreationService creation,
    UnattendedSetup unattended,
    UnattendProfileStore unattendProfiles,
    ProfileValidator software,
    VmPerformanceService performance,
    IHyperVCompute compute,
    IHyperVHost hyperV,
    IMachineBackupPolicies? backups,
    ILogger<MachineProfileVmCreation> logger,
    VmConnectionDefaultsStore? connectionDefaults = null,
    ProfileImageStore? images = null)
{
    /// <summary>The Create VM dialog's defaults, used for whatever the compute section leaves out.</summary>
    public const int DefaultProcessors = 2;
    public const long DefaultStartupMemoryMb = 4096;
    public const long DefaultMaximumMemoryMb = 8192;
    public const int DefaultDiskSizeGb = 64;

    /// <summary>Fixed memory for Performance mode when the profile sets none.</summary>
    public const long DefaultPerformanceMemoryMb = 8192;

    /// <exception cref="MachineProfileNotFoundException">No such profile.</exception>
    /// <exception cref="LifecycleValidationException">The profile does not fit this host or the ISO; Errors lists each problem.</exception>
    /// <exception cref="ResourceWarningsException">Warnings were not acknowledged.</exception>
    public async Task<VmJobSnapshot> StartAsync(
        Guid userId,
        string profileId,
        CreateVmFromProfileRequest request,
        Action<VmJobSnapshot>? onFinished,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = profiles.Get(userId, profileId).Profile;
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var windows = profile.Os == InstallOs.Windows;
        var gpu = windows && profile.Gpu?.Enabled == true ? profile.Gpu : null;

        // Compute, with the Create VM defaults; Performance mode needs fixed memory.
        var memory = profile.Compute?.Memory;
        var dynamic = gpu is null && (memory?.Dynamic ?? true);
        var startupMb = memory?.StartupMb ?? (gpu is null ? DefaultStartupMemoryMb : DefaultPerformanceMemoryMb);
        var maximumMb = dynamic ? memory?.MaximumMb ?? Math.Max(startupMb, DefaultMaximumMemoryMb) : startupMb;
        var processors = profile.Compute?.Processors ?? DefaultProcessors;

        string? switchId = null;
        if (profile.Network?.Switch is { } switchName)
        {
            var switches = await hyperV.ListSwitchesAsync(cancellationToken).ConfigureAwait(false);
            switchId = switches.FirstOrDefault(item => string.Equals(item.Name, switchName, StringComparison.OrdinalIgnoreCase))?.Id;
            if (switchId is null)
            {
                errors.Add(new("network.switch", $"This host has no virtual switch named \"{switchName}\". Add one, or change the profile."));
            }
        }

        (PerformanceSettings Settings, PerformancePlan Plan)? performancePlan = null;
        if (gpu is not null)
        {
            performancePlan = await PlanPerformanceAsync(gpu, processors, startupMb, errors, cancellationToken).ConfigureAwait(false);
        }

        string? backupTarget = null;
        if (profile.Backup is { Enabled: true, Target: { } targetName })
        {
            backupTarget = backups?.FindTarget(targetName);
            if (backupTarget is null)
            {
                warnings.Add(new("backup.target", $"This host has no backup target named \"{targetName}\", so the VM is made without backups."));
            }
        }

        var wallpaper = windows ? WallpaperSource(images, userId, profile) : null;
        var setup = windows ? SetupSnapshot(software, profile, wallpaper, warnings) : null;
        Func<InstallContext, InstallPlan?>? planInstall = null;
        if (profile.Account is { } account)
        {
            var accountProfile = AccountProfile(profileId, profile, account, errors);
            if (accountProfile is not null)
            {
                planInstall = context => unattended.PlanFor(
                    userId, accountProfile, setup, request.WindowsEdition, account.ComputerName, context.IsoPath, context.Inspection,
                    context.VmName, context.SeedPath, context.EnableTpm, context.Errors, context.Warnings, guestPerformanceSetup: gpu is not null,
                    wallpaperPath: setup is null ? null : wallpaper);
            }
        }
        else
        {
            if (setup is not null)
            {
                warnings.Add(new("account", "Software and display settings are applied after an unattended install. With Account off, install the OS from the console and apply them from the VM's menu."));
            }

            if (gpu is not null)
            {
                warnings.Add(new("gpu", "Performance mode is set on the VM, but the GPU driver is copied into the guest only after an unattended install. With Account off, run Set up the guest in the VM's Performance dialog once Windows is installed."));
            }
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        var createRequest = new CreateVmRequest(
            request.Name,
            request.IsoName,
            profile.Compute?.DiskSizeGb ?? DefaultDiskSizeGb,
            processors,
            startupMb,
            maximumMb,
            dynamic,
            switchId,
            profile.Firmware?.Tpm ?? true,
            request.AcknowledgeWarnings);

        var extras = new CreationExtras(
            planInstall,
            profile.Firmware?.SecureBoot ?? true,
            warnings,
            (vmId, context) => ConfigureAsync(vmId, request.Name.Trim(), profile, performancePlan, backupTarget, context));
        return await creation.StartAsync(userId, createRequest, extras, onFinished, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The steps that need the new VM off: processor and network options, Performance mode, and backups.</summary>
    private async Task ConfigureAsync(
        Guid vmId,
        string name,
        MachineProfile profile,
        (PerformanceSettings Settings, PerformancePlan Plan)? performancePlan,
        string? backupTarget,
        VmJobContext context)
    {
        var nested = profile.Compute?.NestedVirtualization;
        var spoofing = profile.Network?.MacAddressSpoofing;
        if (nested is not null || spoofing is not null)
        {
            context.Report("Setting nested virtualization and MAC address spoofing", 82);
            var current = await compute.ReadAsync(vmId, context.Stopping).ConfigureAwait(false);
            var change = new ComputeChange(NestedVirtualization: nested, MacAddressSpoofing: spoofing);
            var desired = current with
            {
                NestedVirtualization = nested ?? current.NestedVirtualization,
                MacAddressSpoofing = spoofing ?? current.MacAddressSpoofing,
            };
            await compute.ApplyAsync(vmId, change, desired, context.Stopping).ConfigureAwait(false);
        }

        if (performancePlan is { } plan)
        {
            context.Report("Adding the GPU partition", 85);
            await performance.ApplyWithinJobAsync(name, vmId, plan.Settings, plan.Plan, context).ConfigureAwait(false);
        }

        if (backupTarget is not null && profile.Backup is { } backup)
        {
            context.Report("Setting the backup policy", 90);
            backups!.Set(vmId, backupTarget, backup);
        }

        if (profile.Display?.Connection is { } connection && connectionDefaults is not null)
        {
            connectionDefaults.Save(new VmConnectionDefaults(vmId, connection, profile.Name, DateTimeOffset.UtcNow));
        }

        logger.LogInformation("Applied the machine profile {Profile} to the new VM {Name} ({VmId}).", profile.Name, name, vmId);
    }

    private async Task<(PerformanceSettings, PerformancePlan)?> PlanPerformanceAsync(
        MachineGpu gpu,
        int processors,
        long memoryMb,
        List<ValidationIssue> errors,
        CancellationToken cancellationToken)
    {
        string? instancePath = null;
        if (gpu.Adapter is { } adapter)
        {
            instancePath = (await performance.FindGpuAsync(adapter, cancellationToken).ConfigureAwait(false))?.InstancePath;
            if (instancePath is null)
            {
                errors.Add(new("gpu.adapter", $"This host has no partitionable GPU named like \"{adapter}\"."));
                return null;
            }
        }

        var share = gpu.Share ?? new MachineGpuShare();
        var settings = new PerformanceSettings(
            processors,
            memoryMb,
            new GpuPartitionShare(instancePath, share.VramPercent, share.EncodePercent, share.DecodePercent, share.ComputePercent),
            null,
            null,
            new PerformanceRdpSettings(gpu.HardwareEncoding ?? false),
            AcknowledgeWarnings: true);
        try
        {
            return await performance.CheckAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (LifecycleValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(issue => issue with { Field = "gpu." + issue.Field }));
        }
        catch (LifecycleConflictException ex)
        {
            errors.Add(new("gpu", ex.Message));
        }

        return null;
    }

    /// <summary>The account section as an unattended install profile, checked as one; null when it has problems.</summary>
    private UnattendProfile? AccountProfile(string profileId, MachineProfile profile, MachineAccount account, List<ValidationIssue> errors)
    {
        try
        {
            return unattendProfiles.Normalize($"machine-{profileId}", new UnattendProfileRequest(
                profile.Name,
                profile.Os,
                account.AdminAccountName ?? UnattendProfile.DefaultAdminAccountName,
                account.TimeZone,
                account.Locale ?? UnattendProfile.DefaultLocale,
                profile.Os == InstallOs.Windows ? account.Windows ?? new WindowsInstallSettings(MachineProfileValidator.DefaultWindowsEdition) : null,
                profile.Os == InstallOs.Linux ? account.Linux ?? new LinuxInstallSettings() : null));
        }
        catch (LifecycleValidationException ex)
        {
            errors.AddRange(ex.Errors.Select(issue => issue with { Field = "account." + issue.Field }));
            return null;
        }
    }

    /// <summary>
    /// The software sections and the guest display settings as the setup profile the install applies when ready,
    /// or null when the profile has neither.
    /// </summary>
    /// <param name="wallpaperPath">The wallpaper image's file on the host, or null when the User has no such image.</param>
    internal static SetupProfile? SetupSnapshot(ProfileValidator software, MachineProfile profile, string? wallpaperPath, List<ValidationIssue> warnings)
    {
        var display = GuestDisplayTweaks(profile.Display?.Guest, wallpaperPath, warnings);
        var tweaks = (profile.Tweaks ?? []).Concat(display).ToList();
        var empty = (profile.Install?.Count ?? 0) == 0 && tweaks.Count == 0 && profile.Browser is null
            && (profile.Remove?.Appx?.Count ?? 0) + (profile.Remove?.Capabilities?.Count ?? 0) + (profile.Remove?.Features?.Count ?? 0) == 0;
        if (empty)
        {
            return null;
        }

        return software.Normalize(new SetupProfile(profile.Name, profile.Description, profile.Install, profile.Remove, tweaks, profile.Browser));
    }

    /// <summary>The wallpaper image's file on the host, or null when the profile names none or the User has no such image.</summary>
    internal static string? WallpaperSource(ProfileImageStore? images, Guid userId, MachineProfile profile) =>
        profile.Display?.Guest?.Wallpaper?.Image is { } name ? images?.PathOf(userId, name) : null;

    /// <summary>
    /// The guest display settings as registry tweaks in the User's account. They show after the next sign-in. A
    /// wallpaper image points to its copy in <see cref="ProfileImageStore.GuestFolder"/>, which the caller copies there.
    /// </summary>
    internal static IReadOnlyList<ProfileTweak> GuestDisplayTweaks(MachineGuestDisplay? guest, string? wallpaperPath, List<ValidationIssue> warnings)
    {
        if (guest is null)
        {
            return [];
        }

        const string personalize = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        var tweaks = new List<ProfileTweak>();
        switch (guest.Theme)
        {
            case GuestTheme.Dark:
                tweaks.Add(new ProfileTweak("appearance.darkMode", "Use dark mode"));
                break;
            case GuestTheme.Light:
                tweaks.Add(Registry(personalize, "AppsUseLightTheme", RegistryValueType.Dword, "1", "Use light mode for apps"));
                tweaks.Add(Registry(personalize, "SystemUsesLightTheme", RegistryValueType.Dword, "1", "Use light mode for Windows"));
                break;
        }

        if (guest.AccentColor is { } accent)
        {
            // Windows keeps the accent as ABGR.
            var abgr = Abgr(accent);
            tweaks.Add(Registry(@"HKCU\Software\Microsoft\Windows\DWM", "AccentColor", RegistryValueType.Dword, abgr, $"Accent color {accent}"));
            tweaks.Add(Registry(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentColorMenu", RegistryValueType.Dword, abgr, $"Accent color {accent} for Start"));
        }

        if (guest.Wallpaper?.Color is { } color)
        {
            var (r, g, b) = Rgb(color);
            tweaks.Add(Registry(@"HKCU\Control Panel\Colors", "Background", RegistryValueType.String, $"{r} {g} {b}", $"Wallpaper color {color}"));
            tweaks.Add(Registry(@"HKCU\Control Panel\Desktop", "WallPaper", RegistryValueType.String, "", "No wallpaper image"));
        }
        else if (guest.Wallpaper?.Image is { } image)
        {
            if (wallpaperPath is null)
            {
                warnings.Add(new("display.guest.wallpaper.image", $"You have no wallpaper image named {image} on this host, so the wallpaper is left as it is. Add it in the profile's Display section."));
            }
            else
            {
                const string desktop = @"HKCU\Control Panel\Desktop";
                tweaks.Add(Registry(desktop, "WallPaper", RegistryValueType.String, ProfileImageStore.GuestPath(image), $"Wallpaper {image}"));
                tweaks.Add(Registry(desktop, "WallpaperStyle", RegistryValueType.String, "10", "Wallpaper fills the screen"));
                tweaks.Add(Registry(desktop, "TileWallpaper", RegistryValueType.String, "0", "Wallpaper is not tiled"));
            }
        }

        return tweaks;
    }

    private static ProfileTweak Registry(string key, string name, RegistryValueType type, string value, string label) =>
        new(Name: label, Registry: new RegistryTweak(key, name, type, value));

    private static (int R, int G, int B) Rgb(string color) => (
        int.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        int.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

    private static string Abgr(string color)
    {
        var (r, g, b) = Rgb(color);
        var value = (0xFFu << 24) | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
        return value.ToString(CultureInfo.InvariantCulture);
    }
}
