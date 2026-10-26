using System.Collections.Concurrent;
using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>Who an administrator window is for: a paired device, or null fields when the host opens one itself.</summary>
public sealed record GuestAdminRequester(Guid? DeviceId, string? DeviceName);

/// <summary>A request about an administrator window that cannot proceed in the VM's state (409, with a problem code).</summary>
public sealed class GuestAdminConflictException(string message, string code) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// The audit shape of administrator windows. The API's audited routes and the host's own closes use the same action
/// names and the same summary: the account and the expiry, plus the reason for a close. Never the passphrase.
/// </summary>
public static class GuestAdminAudit
{
    public const string OpenAction = "openGuestAdmin";
    public const string CloseAction = "closeGuestAdmin";

    public static string Detail(string accountName, DateTimeOffset expiresAt, string? reason = null) =>
        $"account={accountName}, expiresAt={expiresAt:u}" + (reason is null ? string.Empty : $", reason={reason}");
}

/// <summary>
/// Short administrator windows inside Windows guests. Each User has a local administrator account in each VM
/// (<see cref="GuestAdminAccountName"/>), created on first use through PowerShell Direct with the VM's stored
/// administrator credential and disabled whenever no window is open. Opening a window enables it with a one-time
/// passphrase for the User to type into a User Account Control prompt; closing it (expiry, the User, the VM stopping,
/// or a host restart) sets a random password and disables it.
/// <para>
/// Windows for the same VM and User are serialized, and opening one while it is open returns the same passphrase.
/// The passphrase lives only in memory, as a char array zeroed when the window ends; it is never logged or persisted.
/// <see cref="GuestAdminStore"/> records which windows may still be enabled in a guest (without the passphrase), so
/// one the host could not close at once, because the VM stopped or the host restarted, is closed when the guest
/// answers again (<see cref="SweepAsync"/>).
/// </para>
/// <para>
/// <see cref="OpenAsync"/> takes the requester rather than an HTTP request, so a later phase can open a window from a
/// host-side event (a guest agent's approval request) with the same account and audit shape.
/// </para>
/// </summary>
public sealed class GuestAdminService : IDisposable
{
    /// <summary>How often the host service calls <see cref="SweepAsync"/>; also how soon a stopped VM's window ends.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly IVmInventory _inventory;
    private readonly UserStore _users;
    private readonly ProvisioningStore _provisioning;
    private readonly VmCredentialStore _credentials;
    private readonly IGuestAccountManager _guest;
    private readonly GuestAdminStore _store;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<GuestAdminService> _logger;
    private readonly ConcurrentDictionary<(Guid VmId, Guid UserId), Entry> _entries = new();
    private readonly object _backgroundGate = new();
    private Task _background = Task.CompletedTask;

    public GuestAdminService(
        IVmInventory inventory,
        UserStore users,
        ProvisioningStore provisioning,
        VmCredentialStore credentials,
        IGuestAccountManager guest,
        GuestAdminStore store,
        IAuditLog audit,
        TimeProvider time,
        ILogger<GuestAdminService> logger)
    {
        _inventory = inventory;
        _users = users;
        _provisioning = provisioning;
        _credentials = credentials;
        _guest = guest;
        _store = store;
        _audit = audit;
        _time = time;
        _logger = logger;

        // Windows an earlier run left open: their passphrases ended with that process, so each account is disabled
        // as soon as its guest answers.
        foreach (var record in _store.List().Where(record => !record.ClosePending))
        {
            _store.Save(record with { ClosePending = true, CloseReason = GuestAdminCloseReason.HostStartup });
        }
    }

    public GuestAdminSettings Settings => new(_store.WindowMinutes);

    /// <exception cref="ArgumentOutOfRangeException">The length is outside 2 to 30 minutes.</exception>
    public GuestAdminSettings SetSettings(GuestAdminSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.WindowMinutes is < GuestAdminSettings.MinimumMinutes or > GuestAdminSettings.MaximumMinutes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                $"The window length must be from {GuestAdminSettings.MinimumMinutes} to {GuestAdminSettings.MaximumMinutes} minutes.");
        }

        _store.SetWindowMinutes(settings.WindowMinutes);
        return Settings;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<GuestAdminStatus> GetStatusAsync(Guid vmId, Guid userId, CancellationToken cancellationToken)
    {
        _ = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        var accountName = AccountName(userId);
        if (_entries.TryGetValue((vmId, userId), out var entry) && entry.IsOpen(_time.GetUtcNow()))
        {
            return new GuestAdminStatus(accountName, true, entry.ExpiresAt, false);
        }

        return new GuestAdminStatus(accountName, false, null, _store.Find(vmId, userId) is not null);
    }

    /// <summary>Opens a window, or returns the open one unchanged.</summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="GuestAdminConflictException">
    /// The VM runs Linux, is not running, is not provisioned for the User, or PowerShell Direct cannot reach it.
    /// </exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the stored administrator credential.</exception>
    /// <exception cref="GuestOperationException">A guest command failed, or the account could not be verified.</exception>
    public async Task<GuestAdminWindow> OpenAsync(Guid vmId, Guid userId, GuestAdminRequester requester, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requester);
        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        var account = _provisioning.Find(vmId, userId);
        if (vm.GuestOs?.Family == GuestOsFamily.Linux || account?.GuestOs == GuestOsFamily.Linux)
        {
            throw new GuestAdminConflictException(
                $"Administrator access is available for Windows guests only. {vm.Name} runs Linux; use sudo there.",
                ContractInfo.ProblemCodes.GuestAdminUnsupported);
        }

        if (vm.State != VmState.Running)
        {
            throw new GuestAdminConflictException($"{vm.Name} is not running. Start it, then ask for administrator access.", ContractInfo.ProblemCodes.VmNotRunning);
        }

        if (account is null)
        {
            throw new GuestAdminConflictException($"{vm.Name} is not set up for you yet. Set it up first.", ContractInfo.ProblemCodes.VmNotProvisioned);
        }

        var admin = _credentials.Find(vmId)
            ?? throw new GuestAdminConflictException($"{vm.Name} has no stored administrator credential. Set it up again.", ContractInfo.ProblemCodes.VmNotProvisioned);
        var user = _users.Find(userId) ?? throw new InvalidOperationException($"User {userId} does not exist.");
        var accountName = GuestAdminAccountName.For(user.Name);
        var fullName = GuestAdminAccountName.FullName(user.Name);
        var target = account.ToTarget(null);

        var entry = _entries.GetOrAdd((vmId, userId), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (entry.IsOpen(now))
            {
                return new GuestAdminWindow(accountName, new string(entry.Passphrase!), entry.ExpiresAt);
            }

            var expiresAt = now + TimeSpan.FromMinutes(_store.WindowMinutes);
            var record = new GuestAdminRecord(vmId, userId, accountName, expiresAt, requester.DeviceId, requester.DeviceName);

            // Recorded before the guest call, so an account this process enables and never gets to disable is
            // disabled after a restart.
            _store.Save(record);
            var passphrase = PassphraseGenerator.Generate(accountName, fullName);
            GuestAdminAccountState state;
            try
            {
                state = await _guest.EnableAdminAsync(target, admin, accountName, fullName, passphrase, cancellationToken);
            }
            catch (Exception ex)
            {
                // The guest may have enabled the account before the failure.
                CloseLater(record, GuestAdminCloseReason.OpenFailed);
                if (!GuestErrors.IsGuestError(ex))
                {
                    throw;
                }

                var clean = GuestErrors.Sanitize(ex, admin.Password, passphrase);
                throw clean is GuestUnavailableException
                    ? new GuestAdminConflictException(clean.Message, ContractInfo.ProblemCodes.GuestUnavailable)
                    : clean;
            }

            if (!state.IsReadyForElevation)
            {
                CloseLater(record, GuestAdminCloseReason.OpenFailed);
                throw new GuestOperationException(
                    $"The account {accountName} could not be verified in {vm.Name} (exists: {state.Exists}, enabled: {state.Enabled}, " +
                    $"in Administrators: {state.InAdministrators}, in Remote Desktop Users: {state.InRemoteDesktopUsers}, " +
                    $"network sign-in denied: {state.DeniedNetworkLogon}, Remote Desktop sign-in denied: {state.DeniedRemoteInteractiveLogon}).");
            }

            entry.Open(passphrase.ToCharArray(), expiresAt, _time.CreateTimer(_ => OnExpired(vmId, userId, entry), null, expiresAt - now, Timeout.InfiniteTimeSpan));
            _logger.LogInformation(
                "Opened an administrator window for {Account} on {Name} ({VmId}) until {ExpiresAt:u} for {Device}.",
                accountName,
                vm.Name,
                vmId,
                expiresAt,
                requester.DeviceName ?? "the host");
            return new GuestAdminWindow(accountName, passphrase, expiresAt);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Ends the open window now: forgets the passphrase, then disables the account in the guest.</summary>
    /// <returns>False when no window is open.</returns>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="GuestUnavailableException">The guest could not be reached; the account is disabled once it answers.</exception>
    /// <exception cref="GuestCredentialRejectedException">The guest rejected the stored administrator credential.</exception>
    /// <exception cref="GuestOperationException">The guest command failed; it is tried again later.</exception>
    public async Task<bool> CloseAsync(Guid vmId, Guid userId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        if (!_entries.TryGetValue((vmId, userId), out var entry))
        {
            return false;
        }

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (!entry.IsOpen(_time.GetUtcNow()))
            {
                return false;
            }

            entry.Clear();
            var record = MarkPending(vmId, userId, GuestAdminCloseReason.User);
            if (record is null || vm.State != VmState.Running)
            {
                // Nothing in the guest to reach now; the sweep disables the account when the VM runs again.
                return true;
            }

            await DisableAsync(record, cancellationToken);
            _store.Remove(vmId, userId);
            _logger.LogInformation("Closed the administrator window for {Account} on {Name} ({VmId}) at the User's request.", record.AccountName, vm.Name, vmId);
            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Ends windows that expired or whose VM stopped, and disables the accounts of ended windows whose guests answer
    /// again. The host service calls it at startup and every <see cref="SweepInterval"/>.
    /// </summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        foreach (var record in _store.List())
        {
            try
            {
                if (!record.ClosePending && _entries.TryGetValue((record.VmId, record.UserId), out var entry))
                {
                    await EndIfStaleAsync(record, entry, cancellationToken);
                }

                await TryDisableAsync(record.VmId, record.UserId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not check the administrator window for {Account} on VM {VmId}.", record.AccountName, record.VmId);
            }
        }
    }

    /// <summary>Completes when the closes started in the background so far have finished. For tests.</summary>
    internal Task WaitForBackgroundAsync()
    {
        lock (_backgroundGate)
        {
            return _background;
        }
    }

    /// <summary>The open window's passphrase buffer, to check that it is zeroed. For tests.</summary>
    internal char[]? PassphraseBuffer(Guid vmId, Guid userId) =>
        _entries.TryGetValue((vmId, userId), out var entry) ? entry.Passphrase : null;

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Clear();
        }
    }

    private string AccountName(Guid userId) =>
        GuestAdminAccountName.For(_users.Find(userId)?.Name ?? throw new InvalidOperationException($"User {userId} does not exist."));

    private void OnExpired(Guid vmId, Guid userId, Entry entry)
    {
        entry.Gate.Wait();
        try
        {
            if (entry.Passphrase is null || _time.GetUtcNow() < entry.ExpiresAt)
            {
                return;
            }

            entry.Clear();
            if (MarkPending(vmId, userId, GuestAdminCloseReason.Expired) is null)
            {
                return;
            }
        }
        finally
        {
            entry.Gate.Release();
        }

        StartInBackground(vmId, userId);
    }

    /// <summary>Under the entry's gate: ends an open window whose time is up or whose VM is no longer running.</summary>
    private async Task EndIfStaleAsync(GuestAdminRecord record, Entry entry, CancellationToken cancellationToken)
    {
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.Passphrase is null)
            {
                return;
            }

            string reason;
            if (!entry.IsOpen(_time.GetUtcNow()))
            {
                reason = GuestAdminCloseReason.Expired;
            }
            else if (await _inventory.GetAsync(record.VmId, cancellationToken) is not { State: VmState.Running })
            {
                reason = GuestAdminCloseReason.VmStopped;
            }
            else
            {
                return;
            }

            entry.Clear();
            MarkPending(record.VmId, record.UserId, reason);
            _logger.LogInformation("Ended the administrator window for {Account} on VM {VmId} ({Reason}).", record.AccountName, record.VmId, reason);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Disables the account of an ended window when its guest runs; otherwise leaves it for the next sweep.</summary>
    private async Task TryDisableAsync(Guid vmId, Guid userId, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd((vmId, userId), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            if (_store.Find(vmId, userId) is not { ClosePending: true } record || entry.IsOpen(_time.GetUtcNow()))
            {
                return;
            }

            var vm = await _inventory.GetAsync(vmId, cancellationToken);
            if (vm is null)
            {
                _store.Remove(vmId, userId);
                _logger.LogInformation("Forgot the administrator window for {Account} on VM {VmId}: the VM is no longer on this host.", record.AccountName, vmId);
                return;
            }

            if (vm.State != VmState.Running)
            {
                return;
            }

            try
            {
                await DisableAsync(record, cancellationToken);
            }
            catch (GuestUnavailableException)
            {
                // Still starting; the next sweep tries again.
                return;
            }
            catch (Exception ex) when (GuestErrors.IsGuestError(ex))
            {
                _logger.LogWarning("Could not disable {Account} on {Name} ({VmId}); trying again later. {Error}", record.AccountName, vm.Name, vmId, ex.Message);
                return;
            }

            _store.Remove(vmId, userId);
            _logger.LogInformation("Disabled {Account} on {Name} ({VmId}) after its administrator window ended ({Reason}).", record.AccountName, vm.Name, vmId, record.CloseReason);
            WriteAudit(record, vm.Name);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>Sets a random password and disables the account. Throws the sanitized guest error.</summary>
    private async Task DisableAsync(GuestAdminRecord record, CancellationToken cancellationToken)
    {
        var admin = _credentials.Find(record.VmId);
        var account = _provisioning.Find(record.VmId, record.UserId);
        if (admin is null || account is null)
        {
            throw new GuestOperationException($"The VM no longer has a stored administrator credential, so {record.AccountName} cannot be disabled. Set the VM up again.");
        }

        var password = PasswordGenerator.Generate();
        try
        {
            await _guest.DisableAdminAsync(account.ToTarget(null), admin, record.AccountName, password, cancellationToken);
        }
        catch (Exception ex) when (GuestErrors.IsGuestError(ex))
        {
            throw GuestErrors.Sanitize(ex, admin.Password, password);
        }
    }

    /// <summary>Marks the record as ended; returns it, or null when there is none.</summary>
    private GuestAdminRecord? MarkPending(Guid vmId, Guid userId, string reason)
    {
        if (_store.Find(vmId, userId) is not { } record)
        {
            return null;
        }

        record = record with { ClosePending = true, CloseReason = reason };
        _store.Save(record);
        return record;
    }

    /// <summary>Under the entry's gate: marks a window that failed to open as ended and disables it in the background.</summary>
    private void CloseLater(GuestAdminRecord record, string reason)
    {
        _store.Save(record with { ClosePending = true, CloseReason = reason });
        StartInBackground(record.VmId, record.UserId);
    }

    private void StartInBackground(Guid vmId, Guid userId)
    {
        var task = Task.Run(async () =>
        {
            try
            {
                await TryDisableAsync(vmId, userId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not disable the administrator account on VM {VmId}; the next check tries again.", vmId);
            }
        });
        lock (_backgroundGate)
        {
            _background = Task.WhenAll(_background, task);
        }
    }

    /// <summary>Records a close the host made by itself; a close the User asked for is audited by its request.</summary>
    private void WriteAudit(GuestAdminRecord record, string? vmName)
    {
        try
        {
            _audit.Write(new AuditEntry(
                _time.GetUtcNow(),
                GuestAdminAudit.CloseAction,
                AuditOutcome.Succeeded,
                record.UserId,
                _users.Find(record.UserId)?.Name,
                record.DeviceId,
                record.DeviceName,
                record.VmId,
                vmName,
                Detail: GuestAdminAudit.Detail(record.AccountName, record.ExpiresAt, record.CloseReason)));
        }
        catch (AuditUnavailableException ex)
        {
            _logger.LogError(ex, "Could not write the audit entry for disabling {Account} on VM {VmId}.", record.AccountName, record.VmId);
        }
    }

    private sealed class Entry
    {
        private ITimer? _timer;

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public char[]? Passphrase { get; private set; }

        public DateTimeOffset ExpiresAt { get; private set; }

        public bool IsOpen(DateTimeOffset now) => Passphrase is not null && now < ExpiresAt;

        public void Open(char[] passphrase, DateTimeOffset expiresAt, ITimer timer)
        {
            Clear();
            Passphrase = passphrase;
            ExpiresAt = expiresAt;
            _timer = timer;
        }

        public void Clear()
        {
            if (Passphrase is not null)
            {
                Array.Clear(Passphrase);
                Passphrase = null;
            }

            _timer?.Dispose();
            _timer = null;
        }

        public override string ToString() => $"GuestAdminWindow {{ Open = {Passphrase is not null}, ExpiresAt = {ExpiresAt:u} }}";
    }
}
