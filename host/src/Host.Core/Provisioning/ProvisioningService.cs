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
    /// <exception cref="GuestUnavailableException">PowerShell Direct could not reach the guest.</exception>
    /// <exception cref="GuestOperationException">A guest command failed or verification failed.</exception>
    public async Task<VmProvisioning> ProvisionAsync(Guid vmId, Guid userId, ProvisionVmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        if (vm.State != VmState.Running)
        {
            throw new GuestAccountConflictException($"Start {vm.Name} and wait for Windows to finish starting before provisioning it.");
        }

        var user = _users.Find(userId) ?? throw new InvalidOperationException($"User {userId} does not exist.");
        var admin = new GuestCredential(request.AdminUserName.Trim(), request.AdminPassword);

        // The initial password is never stored; each connect rotates it.
        await _guest.ProvisionAsync(vmId, admin, user.VmAccountName, PasswordGenerator.Generate(), request.EnableRemoteDesktop, cancellationToken);

        var state = await _guest.InspectAsync(vmId, admin, user.VmAccountName, cancellationToken);
        if (!state.IsReadyForRemoteDesktop)
        {
            throw new GuestOperationException(
                $"The account {user.VmAccountName} could not be verified in {vm.Name} " +
                $"(exists: {state.Exists}, local: {state.IsLocal}, enabled: {state.Enabled}, Remote Desktop Users: {state.InRemoteDesktopUsers}).");
        }

        var now = _time.GetUtcNow();
        _credentials.Save(vmId, admin);
        _provisioning.Save(new ProvisionedAccount(vmId, userId, user.VmAccountName, now, now));
        _logger.LogInformation("Provisioned {Account} on VM {Name} ({VmId}) for user {UserId}.", user.VmAccountName, vm.Name, vmId, userId);

        return new VmProvisioning(vmId, user.VmAccountName, now);
    }
}
