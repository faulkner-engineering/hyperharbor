using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// One-time setup of a User's local account on a VM. The VM is recorded as provisioned only after
/// the account is verified in the guest, and the administrator credential is stored only then.
/// </summary>
public sealed class ProvisioningService
{
    private readonly IVmInventory _inventory;
    private readonly UserStore _users;
    private readonly IGuestAccountManager _guest;
    private readonly VmCredentialStore _credentials;
    private readonly ProvisioningStore _provisioning;
    private readonly TimeProvider _time;
    private readonly ILogger<ProvisioningService> _logger;

    public ProvisioningService(
        IVmInventory inventory,
        UserStore users,
        IGuestAccountManager guest,
        VmCredentialStore credentials,
        ProvisioningStore provisioning,
        TimeProvider time,
        ILogger<ProvisioningService> logger)
    {
        _inventory = inventory;
        _users = users;
        _guest = guest;
        _credentials = credentials;
        _provisioning = provisioning;
        _time = time;
        _logger = logger;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="GuestAccountConflictException">The VM is not running, or the account exists and is not local.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the administrator credential.</exception>
    /// <exception cref="GuestUnavailableException">PowerShell Direct or SSH could not reach the guest.</exception>
    /// <exception cref="GuestOperationException">A guest command failed or verification failed.</exception>
    public async Task<VmProvisioning> ProvisionAsync(Guid vmId, Guid userId, ProvisionVmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        if (vm.State != VmState.Running)
        {
            throw new GuestAccountConflictException($"Start {vm.Name} and wait for its operating system to finish starting before setting it up.");
        }

        var os = vm.GuestOs?.Family ?? GuestOsFamily.Unknown;
        if (os == GuestOsFamily.Unknown)
        {
            throw new GuestAccountConflictException(
                $"HyperHarbor cannot tell which operating system {vm.Name} runs yet. Wait for it to finish starting; " +
                "the guest must run the Hyper-V data exchange service (built into Windows; hv_kvp_daemon on Linux).");
        }

        var address = ConnectService.PreferredAddress(vm);
        if (os == GuestOsFamily.Linux && address is null)
        {
            throw new GuestAccountConflictException($"{vm.Name} has not reported a network address yet. Wait for it to finish starting.");
        }

        var user = _users.Find(userId) ?? throw new InvalidOperationException($"User {userId} does not exist.");
        var admin = new GuestCredential(request.AdminUserName.Trim(), request.AdminPassword);

        // Setting a VM up again keeps the pinned SSH host key, so a guest that changed its key (or an
        // impostor at its address) does not receive the admin credential. After a reinstall the user
        // opts in with TrustNewHostKey.
        var pinnedKey = os == GuestOsFamily.Linux && !request.TrustNewHostKey ? _provisioning.Find(vmId, userId)?.SshHostKey : null;
        var target = new GuestTarget(vmId, os, address, pinnedKey);
        var options = new GuestProvisionOptions(request.EnableRemoteDesktop, request.InstallDesktop);

        // The initial password is never stored; each connect rotates it.
        var initialPassword = PasswordGenerator.Generate();
        GuestAccountState state;
        try
        {
            target = await _guest.ProvisionAsync(target, admin, user.VmAccountName, initialPassword, options, cancellationToken);
            state = await _guest.InspectAsync(target, admin, user.VmAccountName, cancellationToken);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password, initialPassword);
        }

        if (!state.IsReadyForRemoteDesktop)
        {
            var remoteDesktop = os == GuestOsFamily.Linux ? "xrdp running with a desktop installed" : "in Remote Desktop Users";
            throw new GuestOperationException(
                $"The account {user.VmAccountName} could not be verified in {vm.Name} " +
                $"(exists: {state.Exists}, local: {state.IsLocal}, enabled: {state.Enabled}, {remoteDesktop}: {state.RemoteDesktopAllowed}).");
        }

        var now = _time.GetUtcNow();
        _credentials.Save(vmId, admin);
        _provisioning.Save(new ProvisionedAccount(vmId, userId, user.VmAccountName, now, now, os, target.SshHostKey));
        _logger.LogInformation(
            "Provisioned {Account} on {Os} VM {Name} ({VmId}) for user {UserId}.",
            user.VmAccountName,
            os,
            vm.Name,
            vmId,
            userId);

        return new VmProvisioning(vmId, user.VmAccountName, now);
    }
}
