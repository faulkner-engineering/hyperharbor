using System.Text.RegularExpressions;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>An unattended install the create job will carry out, as validated before the job starts.</summary>
/// <param name="Edition">The Windows image name, in the ISO's own spelling; null for Linux.</param>
/// <param name="MachineName">The Windows computer name or the Linux host name.</param>
/// <param name="UserAccountName">The creating User's VM account, for example hh-owner.</param>
/// <param name="SetupProfile">The setup profile to apply once the install is ready, as it is now; null for none.</param>
public sealed record InstallPlan(UnattendProfile Profile, string? Edition, string MachineName, string SeedPath, string UserAccountName, Shared.Contracts.Profiles.SetupProfile? SetupProfile = null);

/// <summary>
/// The parts of VM creation that unattended installs add: checking the request against the ISO,
/// writing the answer file ISO with a one-time administrator password, storing that password with
/// DPAPI for the host to finish setup and rotate it, recording the install, starting the VM, and
/// getting Windows past "Press any key to boot from CD or DVD".
/// </summary>
public sealed partial class UnattendedSetup
{
    /// <summary>Secure Boot template "MicrosoftUEFICertificateAuthority", which Linux shims are signed for.</summary>
    public const string MicrosoftUefiCaTemplateId = "272e7447-90a4-4563-a4b9-8e4ab00526ce";

    /// <summary>VK_SPACE.</summary>
    public const int SpaceKey = 0x20;

    /// <summary>Windows media wait about five seconds for a key; Space is pressed for longer than that.</summary>
    public const int BootKeyPresses = 12;

    private readonly UnattendProfileStore _profiles;
    private readonly Profiles.SetupProfileStore? _setupProfiles;
    private readonly IsoInspector _inspector;
    private readonly UserStore _users;
    private readonly VmCredentialStore _credentials;
    private readonly UnattendedInstallStore _installs;
    private readonly IHyperVPowerInvoker _power;
    private readonly IVmKeyboard _keyboard;
    private readonly TimeProvider _time;
    private readonly TimeSpan _keyInterval;
    private readonly ILogger<UnattendedSetup> _logger;

    /// <param name="keyInterval">Time between key presses at first boot; one second unless a test shortens it.</param>
    public UnattendedSetup(
        UnattendProfileStore profiles,
        IsoInspector inspector,
        UserStore users,
        VmCredentialStore credentials,
        UnattendedInstallStore installs,
        IHyperVPowerInvoker power,
        IVmKeyboard keyboard,
        TimeProvider time,
        ILogger<UnattendedSetup> logger,
        TimeSpan? keyInterval = null,
        Profiles.SetupProfileStore? setupProfiles = null)
    {
        _setupProfiles = setupProfiles;
        _profiles = profiles;
        _inspector = inspector;
        _users = users;
        _credentials = credentials;
        _installs = installs;
        _power = power;
        _keyboard = keyboard;
        _time = time;
        _keyInterval = keyInterval ?? TimeSpan.FromSeconds(1);
        _logger = logger;
    }

    /// <summary>What the ISO installs, or null when that cannot be told (or the file cannot be read).</summary>
    public IsoInspection Inspect(string isoPath)
    {
        try
        {
            return _inspector.Inspect(isoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Xml.XmlException)
        {
            _logger.LogWarning(ex, "Could not inspect the image {Path}.", isoPath);
            return new IsoInspection(null, null, []);
        }
    }

    /// <summary>Checks an install request against the profile, the ISO, and the User. Problems go into <paramref name="errors"/>.</summary>
    /// <returns>The plan, or null when there were errors.</returns>
    public InstallPlan? Plan(
        Guid userId,
        UnattendedInstallRequest request,
        string isoPath,
        IsoInspection inspection,
        string vmName,
        string seedPath,
        bool enableTpm,
        List<ValidationIssue> errors,
        List<ValidationIssue> warnings)
    {
        UnattendProfile profile;
        try
        {
            profile = _profiles.Get(userId, request.ProfileId ?? string.Empty);
        }
        catch (UnattendProfileNotFoundException)
        {
            errors.Add(new("install.profileId", "The unattended profile was not found. Choose another profile."));
            return null;
        }

        if (inspection.Os is not { } os)
        {
            errors.Add(new("install.profileId", $"HyperHarbor cannot tell what {Path.GetFileName(isoPath)} installs, so it cannot install it unattended. Choose manual install."));
            return null;
        }

        if (os != profile.Os)
        {
            errors.Add(new("install.profileId", $"{Path.GetFileName(isoPath)} installs {os}, but \"{profile.Name}\" is a {profile.Os} profile."));
            return null;
        }

        var user = _users.Find(userId);
        if (user is null)
        {
            errors.Add(new("install.profileId", "Your user was not found on the host."));
            return null;
        }

        string? edition = null;
        string machineName;
        if (os == InstallOs.Windows)
        {
            var wanted = (request.WindowsEdition ?? profile.Windows!.DefaultEdition).Trim();
            edition = inspection.Editions.FirstOrDefault(name => string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase));
            if (edition is null)
            {
                errors.Add(new("install.windowsEdition", $"This ISO has no edition named \"{wanted}\". Choose one of: {string.Join(", ", inspection.Editions)}."));
            }

            machineName = (request.ComputerName ?? AutounattendBuilder.ComputerName(vmName)).Trim();
            if (!ComputerName().IsMatch(machineName) || machineName.All(char.IsAsciiDigit))
            {
                errors.Add(new("install.computerName", "Use 1 to 15 letters, digits, or hyphens, not only digits."));
            }

            if (!enableTpm && !profile.Windows!.BypassHardwareChecks && (edition ?? wanted).StartsWith("Windows 11", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new("enableTpm", "Windows 11 Setup stops on a VM without a TPM unless the profile skips hardware checks. Add a TPM or use a profile that skips them."));
            }
        }
        else
        {
            if (request.WindowsEdition is not null || request.ComputerName is not null)
            {
                errors.Add(new("install", "Edition and computer name apply to Windows installs only."));
            }

            machineName = CloudInitBuilder.HostName(vmName);
        }

        if (File.Exists(seedPath))
        {
            errors.Add(new("name", $"An answer file already exists at {seedPath}. Choose another name or remove the file."));
        }

        var setupProfile = SetupProfileFor(userId, request.SetupProfileId, os, errors);
        return errors.Count > 0 ? null : new InstallPlan(profile, edition, machineName, seedPath, user.VmAccountName, setupProfile);
    }

    /// <summary>The User's setup profile as it is now, so later edits do not change an install in progress.</summary>
    private Shared.Contracts.Profiles.SetupProfile? SetupProfileFor(Guid userId, string? setupProfileId, InstallOs os, List<ValidationIssue> errors)
    {
        if (string.IsNullOrWhiteSpace(setupProfileId))
        {
            return null;
        }

        const string field = "install.setupProfileId";
        if (os != InstallOs.Windows)
        {
            errors.Add(new(field, "Setup profiles apply to Windows installs only."));
            return null;
        }

        if (_setupProfiles is null)
        {
            errors.Add(new(field, "Setup profiles are not available on this host."));
            return null;
        }

        try
        {
            return _setupProfiles.Get(userId, setupProfileId).Profile;
        }
        catch (Profiles.SetupProfileNotFoundException)
        {
            errors.Add(new(field, "The setup profile was not found. Choose another one."));
        }
        catch (Lifecycle.LifecycleValidationException ex)
        {
            errors.Add(new(field, $"The setup profile's file on the host does not read: {string.Join(" ", ex.Errors.Select(issue => issue.Message))}"));
        }

        return null;
    }

    /// <summary>Writes the answer file ISO with a new one-time administrator password.</summary>
    /// <returns>The password, which the job stores once the VM exists.</returns>
    public string WriteSeed(InstallPlan plan)
    {
        var password = PasswordGenerator.Generate();
        if (plan.Profile.Os == InstallOs.Windows)
        {
            SeedIso.ForWindows(plan.SeedPath, new WindowsInstall(plan.Profile, plan.Edition!, plan.MachineName, password, plan.UserAccountName));
        }
        else
        {
            SeedIso.ForLinux(plan.SeedPath, new LinuxInstall(plan.Profile, plan.MachineName, Sha512Crypt.Hash(password)));
        }

        return password;
    }

    /// <summary>Stores the credential, records the install, starts the VM, and presses Space for Windows media.</summary>
    public async Task BeginAsync(Guid vmId, Guid userId, InstallPlan plan, string password, Action<string> report, CancellationToken cancellationToken)
    {
        _credentials.Save(vmId, new GuestCredential(plan.Profile.AdminAccountName, password));
        var now = _time.GetUtcNow();
        var profile = plan.Profile;
        _installs.Save(new UnattendedInstall(
            vmId,
            userId,
            profile.Id,
            profile.Os,
            profile.Linux?.InstallDesktop ?? false,
            plan.SeedPath,
            profile.Os == InstallOs.Linux ? UnattendedInstallState.AwaitingConfirmation : UnattendedInstallState.Installing,
            profile.Os == InstallOs.Linux ? "Type yes in the console to start the install" : "Installing Windows",
            now,
            now,
            SetupProfile: plan.SetupProfile));

        report("Starting the virtual machine");
        await _power.InvokeAsync(vmId, VmAction.Start, cancellationToken).ConfigureAwait(false);

        if (profile.Os == InstallOs.Windows)
        {
            report("Starting Windows Setup");
            await PressAnyKeyAsync(vmId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Started the unattended install of VM {VmId} with profile {Profile}.", vmId, profile.Id);
    }

    /// <summary>Removes what a failed job created: the seed, and the credential and record when the VM existed.</summary>
    public void RollBack(InstallPlan plan, Guid? vmId)
    {
        DeleteSeed(plan.SeedPath);
        if (vmId is { } id)
        {
            _credentials.Remove(id);
            _installs.Remove(id);
        }
    }

    /// <summary>Forgets a deleted VM: its install record, its seed, and its stored administrator credential.</summary>
    public void Forget(Guid vmId)
    {
        if (_installs.Find(vmId) is { } install)
        {
            DeleteSeed(install.SeedPath);
            _installs.Remove(vmId);
        }

        _credentials.Remove(vmId);
    }

    /// <summary>
    /// Windows media ask to "Press any key to boot from CD or DVD" and boot the next device after about
    /// five seconds. Space is pressed once a second for longer than that, on this first boot only, so later
    /// restarts boot the installed system. Presses fail harmlessly while the VM is still starting.
    /// </summary>
    private async Task PressAnyKeyAsync(Guid vmId, CancellationToken cancellationToken)
    {
        for (var press = 0; press < BootKeyPresses; press++)
        {
            await Task.Delay(_keyInterval, _time, cancellationToken).ConfigureAwait(false);
            try
            {
                await _keyboard.TypeKeyAsync(vmId, SpaceKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "A key press for VM {VmId} was not delivered.", vmId);
            }
        }
    }

    private void DeleteSeed(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the answer file {Path}.", path);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9-]{1,15}$")]
    private static partial Regex ComputerName();
}
