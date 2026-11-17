using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// The administrator credential the host keeps for a VM: what it last saw of it, a check against the running guest, and
/// its replacement when it stopped working. Replacing it leaves the User's account alone, unlike setting the VM up
/// again, which resets that account's password and with it the data Windows protected with the old one.
/// </summary>
public sealed class VmAdminCredentialService
{
    /// <summary>How long before a password expires the VM list starts to warn.</summary>
    public static readonly TimeSpan ExpiryWarning = TimeSpan.FromDays(14);

    private readonly IVmInventory _inventory;
    private readonly IGuestAccountManager _guest;
    private readonly VmCredentialStore _credentials;
    private readonly ProvisioningStore _provisioning;
    private readonly VmAdminCredentialStatusStore _status;
    private readonly TimeProvider _time;
    private readonly ILogger<VmAdminCredentialService> _logger;

    public VmAdminCredentialService(
        IVmInventory inventory,
        IGuestAccountManager guest,
        VmCredentialStore credentials,
        ProvisioningStore provisioning,
        VmAdminCredentialStatusStore status,
        TimeProvider time,
        ILogger<VmAdminCredentialService> logger)
    {
        _inventory = inventory;
        _guest = guest;
        _credentials = credentials;
        _provisioning = provisioning;
        _status = status;
        _time = time;
        _logger = logger;
    }

    /// <summary>What the host recorded; the guest is not contacted.</summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<VmAdminCredential> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        _ = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        return Describe(vmId, _credentials.Find(vmId));
    }

    /// <summary>The VM list's warning: the guest refused the credential, or its password expires soon.</summary>
    public VmAdminCredentialAlert? AlertFor(Guid vmId) => Alert(_status.Find(vmId), _time.GetUtcNow());

    internal static VmAdminCredentialAlert? Alert(AdminCredentialStatus? status, DateTimeOffset now) => status switch
    {
        { State: VmAdminCredentialState.Rejected } => new VmAdminCredentialAlert(VmAdminCredentialAlertKind.Rejected, status.PasswordExpiresAt),
        { PasswordExpiresAt: { } expires } when expires - now <= ExpiryWarning => new VmAdminCredentialAlert(VmAdminCredentialAlertKind.Expiring, expires),
        _ => null,
    };

    /// <summary>
    /// Signs in to the running guest with the stored credential and records what it answered. A refusal is a result
    /// (state rejected), not an error.
    /// </summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not running, has no stored credential, or cannot be reached yet.</exception>
    /// <exception cref="GuestUnavailableException">PowerShell Direct or SSH could not reach the guest.</exception>
    /// <exception cref="GuestOperationException">The guest could not turn off password expiry when asked to.</exception>
    public async Task<VmAdminCredential> CheckAsync(Guid vmId, bool passwordNeverExpires, CancellationToken cancellationToken)
    {
        var vm = await RequireRunningAsync(vmId, "check its administrator account", cancellationToken).ConfigureAwait(false);
        var admin = _credentials.Find(vmId)
            ?? throw new LifecycleConflictException(
                $"{vm.Name} has no stored administrator account. Use Set up… to give HyperHarbor one.",
                ContractInfo.ProblemCodes.VmNotProvisioned);
        var target = TargetFor(vm);

        GuestAdministrator administrator;
        try
        {
            administrator = await _guest.CheckAdministratorAsync(target, admin, passwordNeverExpires, cancellationToken).ConfigureAwait(false);
        }
        catch (GuestCredentialRejectedException ex)
        {
            var reason = GuestErrors.Clean(ex.Message, admin.Password);
            _status.RecordRejected(vmId, reason, _time.GetUtcNow());
            _logger.LogWarning("{Name} ({VmId}) rejected the stored administrator credential for {Account}: {Reason}", vm.Name, vmId, admin.UserName, reason);
            return Describe(vmId, admin);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password);
        }

        if (!administrator.IsAdministrator)
        {
            _status.RecordRejected(vmId, NotAnAdministrator(admin.UserName, vm.Name), _time.GetUtcNow());
            return Describe(vmId, admin);
        }

        _status.RecordWorking(vmId, administrator, _time.GetUtcNow());
        ThrowIfStillExpiring(vm, target, admin, administrator, passwordNeverExpires, stored: false);
        return Describe(vmId, admin);
    }

    /// <summary>
    /// Stores another administrator credential for the VM once the running guest accepts it and the account is an
    /// administrator there. The User's account in the guest is not touched.
    /// </summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleValidationException">The user name or password is empty or too long.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not running, or cannot be reached yet.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the credential, or the account is not an administrator.</exception>
    /// <exception cref="GuestUnavailableException">PowerShell Direct or SSH could not reach the guest.</exception>
    /// <exception cref="GuestOperationException">The credential was stored, but the guest could not turn off password expiry when asked to.</exception>
    public async Task<VmAdminCredential> ReplaceAsync(Guid vmId, SetVmAdminCredentialRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var userName = (request.UserName ?? string.Empty).Trim();
        var errors = new List<ValidationIssue>();
        if (userName.Length is 0 or > SetVmAdminCredentialRequest.MaxLength)
        {
            errors.Add(new("userName", $"Enter the administrator account's name (at most {SetVmAdminCredentialRequest.MaxLength} characters)."));
        }

        if (request.Password is not { Length: > 0 and <= SetVmAdminCredentialRequest.MaxLength })
        {
            errors.Add(new("password", $"Enter the account's password (at most {SetVmAdminCredentialRequest.MaxLength} characters)."));
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException("The administrator credential is not valid.", errors);
        }

        var vm = await RequireRunningAsync(vmId, "store its administrator account", cancellationToken).ConfigureAwait(false);
        var target = TargetFor(vm);

        // The account keeps its origin: HyperHarbor's own account stays managed when only its password is new.
        var stored = _credentials.Find(vmId);
        var candidate = new GuestCredential(userName, request.Password);
        candidate = candidate with { Managed = stored is { Managed: true } && candidate.IsSameAccount(stored) };

        GuestAdministrator administrator;
        try
        {
            administrator = await _guest.CheckAdministratorAsync(target, candidate, request.PasswordNeverExpires, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, candidate.Password);
        }

        if (!administrator.IsAdministrator)
        {
            throw new GuestCredentialRejectedException($"{NotAnAdministrator(userName, vm.Name)} HyperHarbor needs an administrator account.");
        }

        _credentials.Save(vmId, candidate);
        _status.RecordWorking(vmId, administrator, _time.GetUtcNow());
        _logger.LogInformation("Stored a new administrator credential for {Name} ({VmId}): account {Account}.", vm.Name, vmId, userName);
        ThrowIfStillExpiring(vm, target, candidate, administrator, request.PasswordNeverExpires, stored: true);
        return Describe(vmId, candidate);
    }

    private VmAdminCredential Describe(Guid vmId, GuestCredential? admin)
    {
        if (admin is null)
        {
            return new VmAdminCredential(null, false, VmAdminCredentialState.Unchecked, null, null, null);
        }

        var status = _status.Find(vmId);
        return new VmAdminCredential(
            admin.UserName,
            admin.Managed,
            status?.State ?? VmAdminCredentialState.Unchecked,
            status?.CheckedAt,
            status?.PasswordExpiresAt,
            status is { State: VmAdminCredentialState.Rejected } ? status.Detail : null);
    }

    private async Task<Vm> RequireRunningAsync(Guid vmId, string action, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        return vm.State == VmState.Running
            ? vm
            : throw new LifecycleConflictException($"{vm.Name} is not running. Start it, then {action}.", ContractInfo.ProblemCodes.VmNotRunning);
    }

    /// <summary>
    /// How to reach the guest. A Linux guest is reached only with the SSH host key pinned when it was set up, so a
    /// credential is never sent to a machine that merely answers at its address.
    /// </summary>
    private GuestTarget TargetFor(Vm vm)
    {
        var accounts = _provisioning.ForVm(vm.Id);
        var os = vm.GuestOs?.Family is { } family and not GuestOsFamily.Unknown
            ? family
            : accounts.Select(account => (GuestOsFamily?)account.GuestOs).FirstOrDefault() ?? GuestOsFamily.Unknown;
        switch (os)
        {
            case GuestOsFamily.Windows:
                return new GuestTarget(vm.Id, GuestOsFamily.Windows);
            case GuestOsFamily.Linux:
                var address = ConnectService.PreferredAddress(vm)
                    ?? throw new LifecycleConflictException($"{vm.Name} has not reported a network address yet. Wait for it to finish starting.");
                var hostKey = accounts.Select(account => account.SshHostKey).FirstOrDefault(key => key is not null)
                    ?? throw new LifecycleConflictException(
                        $"{vm.Name} was never set up, so its SSH host key is not known. Use Set up… instead.",
                        ContractInfo.ProblemCodes.VmNotProvisioned);
                return new GuestTarget(vm.Id, GuestOsFamily.Linux, address, hostKey);
            default:
                throw new LifecycleConflictException(
                    $"HyperHarbor cannot tell which operating system {vm.Name} runs yet. Wait for it to finish starting.");
        }
    }

    /// <summary>The user asked for password expiry to be turned off, and the guest still reports a date.</summary>
    private static void ThrowIfStillExpiring(Vm vm, GuestTarget target, GuestCredential admin, GuestAdministrator administrator, bool requested, bool stored)
    {
        if (!requested || target.Os != GuestOsFamily.Windows || administrator.PasswordExpiresAt is null)
        {
            return;
        }

        var reason = GuestErrors.Clean(administrator.Problem ?? "The guest did not say why.", admin.Password);
        throw new GuestOperationException(
            $"{(stored ? "The credential works and was stored, but password" : "Password")} expiry could not be turned off for {admin.UserName} in {vm.Name}. {reason}");
    }

    private static string NotAnAdministrator(string userName, string vmName) =>
        $"{userName} signed in to {vmName} but is not an administrator there.";
}
