using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Profiles;
using HyperHarbor.Shared.Contracts.Unattend;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>The "Install" configuration section.</summary>
public sealed class InstallWatcherOptions
{
    public const string SectionName = "Install";

    /// <summary>How often active installs are checked.</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Running time (not wall time) after which an install that has not finished is failed.</summary>
    public int TimeoutMinutes { get; set; } = 180;

    /// <summary>Attempts to set up the User's account before the install is failed.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>
/// Follows unattended installs until the User can connect. On each tick, for every active install, it
/// reads the VM from the inventory: the guest's OS appears through Hyper-V data exchange once the
/// installed system runs, then its address, then Remote Desktop (Windows) or SSH (Linux) must answer
/// a real protocol greeting. Only then does it set up the User's account with the one-time
/// administrator credential, require Remote Desktop to answer again, rotate the administrator's
/// password (it was in the answer file), and eject and delete the answer file ISO. Records are on
/// disk, so a host restart resumes where it left off. Setting up runs in the background, so one slow
/// guest (Linux package installs take minutes) does not hold up the others.
/// </summary>
public sealed class UnattendedInstallWatcher
{
    private readonly UnattendedInstallStore _installs;
    private readonly IVmInventory _inventory;
    private readonly IRemoteAccessProbe _probe;
    private readonly ProvisioningService _provisioning;
    private readonly ProvisioningStore _accounts;
    private readonly VmCredentialStore _credentials;
    private readonly IGuestAccountManager _guest;
    private readonly IVmMedia _media;
    private readonly TimeProvider _time;
    private readonly InstallWatcherOptions _options;
    private readonly ILogger<UnattendedInstallWatcher> _logger;
    private readonly AppxInventoryService? _appx;
    private readonly ConcurrentDictionary<Guid, Task> _configuring = new();
    private DateTimeOffset? _lastTick;

    public UnattendedInstallWatcher(
        UnattendedInstallStore installs,
        IVmInventory inventory,
        IRemoteAccessProbe probe,
        ProvisioningService provisioning,
        ProvisioningStore accounts,
        VmCredentialStore credentials,
        IGuestAccountManager guest,
        IVmMedia media,
        TimeProvider time,
        InstallWatcherOptions options,
        ILogger<UnattendedInstallWatcher> logger,
        AppxInventoryService? appx = null)
    {
        _appx = appx;
        _installs = installs;
        _inventory = inventory;
        _probe = probe;
        _provisioning = provisioning;
        _accounts = accounts;
        _credentials = credentials;
        _guest = guest;
        _media = media;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Checks every active install once.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var elapsed = _lastTick is { } last ? now - last : TimeSpan.Zero;
        _lastTick = now;

        foreach (var install in _installs.List().Where(install => install.IsActive))
        {
            if (_configuring.ContainsKey(install.VmId))
            {
                continue;
            }

            try
            {
                await StepAsync(install, now, elapsed, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HyperVUnavailableException or HyperVCallException)
            {
                _logger.LogWarning(ex, "Could not check the unattended install of VM {VmId}; trying again on the next pass.", install.VmId);
            }
        }
    }

    /// <summary>Waits for account setups started by earlier ticks. Exposed for tests.</summary>
    internal Task WhenConfiguredAsync() => Task.WhenAll(_configuring.Values);

    /// <summary>Stops following an install and removes its answer file. The VM and its credential stay.</summary>
    /// <returns>False when the VM has no active install.</returns>
    public async Task<bool> CancelAsync(Guid vmId, CancellationToken cancellationToken)
    {
        if (_installs.Find(vmId) is not { IsActive: true } install || _configuring.ContainsKey(vmId))
        {
            return false;
        }

        await RemoveSeedAsync(install, cancellationToken).ConfigureAwait(false);
        Save(install, UnattendedInstallState.Canceled, "Canceled. Finish setting up the VM from its console.");
        return true;
    }

    private async Task StepAsync(UnattendedInstall install, DateTimeOffset now, TimeSpan elapsed, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(install.VmId, cancellationToken).ConfigureAwait(false);
        if (vm is null)
        {
            Fail(install, "The VM no longer exists.");
            return;
        }

        if (vm.State != VmState.Running)
        {
            if (install.Step != OffStep)
            {
                Save(install, install.State, OffStep);
            }

            return;
        }

        install = install with { RunningSeconds = install.RunningSeconds + (long)elapsed.TotalSeconds };
        if (install.RunningSeconds > _options.TimeoutMinutes * 60L)
        {
            Fail(install, $"The install did not finish within {_options.TimeoutMinutes} minutes of running time. Open the console to see where it stopped.");
            return;
        }

        if (install.NextAttemptAt is { } next && now < next)
        {
            _installs.Save(install);
            return;
        }

        // The installed system reports its OS through data exchange; Setup and the live installer do not.
        if ((vm.GuestOs?.Family ?? GuestOsFamily.Unknown) == GuestOsFamily.Unknown)
        {
            if (install.State == UnattendedInstallState.AwaitingConfirmation)
            {
                Save(install, install.State, "Installing Ubuntu. If the console asks \"Continue with autoinstall?\", type yes.");
            }
            else
            {
                Save(install, UnattendedInstallState.Installing, install.Os == InstallOs.Windows ? "Installing Windows" : "Installing Ubuntu");
            }

            return;
        }

        if (Ipv4(vm) is not { } address)
        {
            Save(install, UnattendedInstallState.WaitingForGuest, "Waiting for the guest to report a network address");
            return;
        }

        var answers = install.Os == InstallOs.Windows
            ? await _probe.RdpAnswersAsync(address, cancellationToken).ConfigureAwait(false)
            : await _probe.SshAnswersAsync(address, cancellationToken).ConfigureAwait(false);
        if (!answers)
        {
            Save(install, UnattendedInstallState.WaitingForRemoteAccess,
                install.Os == InstallOs.Windows ? "Waiting for Remote Desktop to answer" : "Waiting for SSH to answer");
            return;
        }

        var configuring = Save(install, UnattendedInstallState.Configuring,
            install.Os == InstallOs.Linux && install.InstallDesktop ? "Setting up your account and the desktop (several minutes)" : "Setting up your account");
        _configuring[install.VmId] = Task.Run(async () =>
        {
            try
            {
                await ConfigureAsync(configuring, address).ConfigureAwait(false);
            }
            finally
            {
                _configuring.TryRemove(install.VmId, out _);
            }
        }, CancellationToken.None);
    }

    private async Task ConfigureAsync(UnattendedInstall install, string address)
    {
        var admin = _credentials.Find(install.VmId);
        if (admin is null)
        {
            Fail(install, "The one-time administrator credential is missing, so the account cannot be set up. Use Set up… instead.");
            return;
        }

        var rotated = PasswordGenerator.Generate();
        try
        {
            // A Linux profile without a desktop has no Remote Desktop to set up; SSH answering is the end.
            if (install.Os == InstallOs.Linux && !install.InstallDesktop)
            {
                await _guest.SetPasswordAsync(new GuestTarget(install.VmId, GuestOsFamily.Linux, address), admin, admin.UserName, rotated, CancellationToken.None).ConfigureAwait(false);
                _credentials.Save(install.VmId, admin with { Password = rotated });
                await RemoveSeedAsync(install, CancellationToken.None).ConfigureAwait(false);
                Save(install with { Error = null, NextAttemptAt = null }, UnattendedInstallState.Ready, "Ready. This profile installs no desktop, so use SSH.");
                return;
            }

            await _provisioning.ProvisionAsync(
                install.VmId,
                install.UserId,
                new ProvisionVmRequest(admin.UserName, admin.Password, EnableRemoteDesktop: true, InstallDesktop: install.InstallDesktop),
                CancellationToken.None,
                requireRemoteDesktop: _probe).ConfigureAwait(false);

            // The password was in the answer file, so it is replaced as soon as the account works.
            var account = _accounts.Find(install.VmId, install.UserId)!;
            await _guest.SetPasswordAsync(account.ToTarget(address), admin, admin.UserName, rotated, CancellationToken.None).ConfigureAwait(false);
            _credentials.Save(install.VmId, admin with { Password = rotated });
        }
        catch (GuestCredentialRejectedException ex)
        {
            Fail(install, GuestErrors.Clean(ex.Message, admin.Password, rotated));
            return;
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex) || ex is GuestAccountConflictException or HyperVUnavailableException or HyperVCallException)
        {
            Retry(install, GuestErrors.Clean(ex.Message, admin.Password, rotated));
            return;
        }

        await RemoveSeedAsync(install, CancellationToken.None).ConfigureAwait(false);
        Save(install with { Error = null, NextAttemptAt = null }, UnattendedInstallState.Ready, "Ready");
        _logger.LogInformation("The unattended install of VM {VmId} is ready.", install.VmId);
        if (install.Os == InstallOs.Windows)
        {
            await RecordAppxBaselineAsync(install.VmId).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A fresh unattended install is the cleanest Windows there is, so it becomes the Appx baseline for its build
    /// and edition unless one exists. Failing to record it never fails the install.
    /// </summary>
    private async Task RecordAppxBaselineAsync(Guid vmId)
    {
        if (_appx is null)
        {
            return;
        }

        try
        {
            await _appx.RecordBaselineAsync(vmId, AppxBaselineSource.UnattendedInstall, onlyIfMissing: true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex) || ex is LifecycleConflictException or HyperVUnavailableException or HyperVCallException or IOException)
        {
            _logger.LogWarning("The clean Appx baseline was not recorded from VM {VmId}: {Error}", vmId, ex.Message);
        }
    }

    /// <summary>Backs off 1, 2, 4, 8 minutes between attempts, then fails.</summary>
    private void Retry(UnattendedInstall install, string error)
    {
        var attempts = install.Attempts + 1;
        if (attempts >= _options.MaxAttempts)
        {
            Fail(install with { Attempts = attempts }, $"Setting up the account failed {attempts} times. Last error: {error}");
            return;
        }

        var delay = TimeSpan.FromMinutes(Math.Pow(2, attempts - 1));
        _logger.LogWarning("Setting up the account on VM {VmId} failed (attempt {Attempt}); trying again in {Delay}: {Error}", install.VmId, attempts, delay, error);
        Save(
            install with { Attempts = attempts, NextAttemptAt = _time.GetUtcNow() + delay, Error = error },
            UnattendedInstallState.WaitingForRemoteAccess,
            $"Setting up the account failed; trying again in {delay.TotalMinutes:0} min.");
    }

    private void Fail(UnattendedInstall install, string error)
    {
        _logger.LogWarning("The unattended install of VM {VmId} failed: {Error}", install.VmId, error);
        Save(install with { Error = error }, UnattendedInstallState.Failed, "Install failed");
    }

    private async Task RemoveSeedAsync(UnattendedInstall install, CancellationToken cancellationToken)
    {
        try
        {
            await _media.EjectAsync(install.VmId, install.SeedPath, cancellationToken).ConfigureAwait(false);
            File.Delete(install.SeedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HyperVUnavailableException or HyperVCallException
            or HyperVJobFailedException or Power.HyperVOperationException)
        {
            // The ISO only holds the one-time password, which has been replaced; deleting the VM removes the file too.
            _logger.LogWarning(ex, "Could not remove the answer file {Path} from VM {VmId}.", install.SeedPath, install.VmId);
        }
    }

    private UnattendedInstall Save(UnattendedInstall install, UnattendedInstallState state, string step)
    {
        var changed = install.State != state || install.Step != step;
        var saved = changed ? install with { State = state, Step = step, UpdatedAt = _time.GetUtcNow() } : install;
        _installs.Save(saved);
        return saved;
    }

    private const string OffStep = "The VM is off. Start it to continue the install.";

    private static string? Ipv4(Vm vm) =>
        vm.IpAddresses.FirstOrDefault(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
}
