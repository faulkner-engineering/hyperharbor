using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <param name="ExpiresAt">End of the reuse window.</param>
/// <param name="Rotated">False when the guest rejected the administrator credential and this is the password set at the last successful rotation.</param>
public sealed record RotatedPassword(string Password, DateTimeOffset ExpiresAt, bool Rotated = true)
{
    public override string ToString() => $"RotatedPassword {{ ExpiresAt = {ExpiresAt:u}, Rotated = {Rotated} }}";
}

/// <summary>
/// Rotates a User's VM account password on connect, by changing it from the password set last time (kept in
/// <see cref="VmAccountPasswordStore"/>), so the account keeps the data Windows protected with it; the first rotation,
/// or one whose stored password no longer works, resets it instead. Rotations for the same VM and User are
/// serialized, and callers within the reuse window get the password that was just set, so two
/// devices connecting together do not invalidate each other's credential. The password is held
/// only in memory, as a char array that is zeroed when the window ends; it is never logged.
/// <para>
/// When the guest rejects the stored administrator credential (its password expired or was changed in the guest), the
/// rotation cannot happen. The User is not locked out for that: the password set last time is still the account's
/// password, so it is issued again, marked as not rotated, and the rejection is recorded for the VM list
/// (<see cref="VmAdminCredentialStatusStore"/>). Only a connect without an earlier password fails: the first one
/// after the VM moved here from another host, whose records do not include it.
/// </para>
/// </summary>
public sealed class PasswordRotator : IDisposable
{
    public static readonly TimeSpan DefaultReuseWindow = TimeSpan.FromSeconds(60);

    private readonly IGuestAccountManager _guest;
    private readonly VmCredentialStore _credentials;
    private readonly VmAccountPasswordStore _passwords;
    private readonly TimeProvider _time;
    private readonly TimeSpan _reuseWindow;
    private readonly ILogger<PasswordRotator> _logger;
    private readonly VmAdminCredentialStatusStore? _status;
    private readonly ConcurrentDictionary<(Guid VmId, Guid UserId), Entry> _entries = new();

    public PasswordRotator(
        IGuestAccountManager guest,
        VmCredentialStore credentials,
        VmAccountPasswordStore passwords,
        TimeProvider time,
        TimeSpan reuseWindow,
        ILogger<PasswordRotator> logger,
        VmAdminCredentialStatusStore? status = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reuseWindow, TimeSpan.Zero);
        _status = status;
        _guest = guest;
        _credentials = credentials;
        _passwords = passwords;
        _time = time;
        _reuseWindow = reuseWindow;
        _logger = logger;
    }

    /// <exception cref="GuestAccountConflictException">No administrator credential is stored for the VM.</exception>
    public async Task<RotatedPassword> GetPasswordAsync(GuestTarget target, Guid userId, string accountName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var vmId = target.VmId;
        var entry = _entries.GetOrAdd((vmId, userId), _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (entry.Password is { } current && now < entry.ExpiresAt)
            {
                return new RotatedPassword(new string(current), entry.ExpiresAt);
            }

            var admin = _credentials.Find(vmId)
                ?? throw new GuestAccountConflictException("This VM has no stored administrator credential. Provision it again.");

            var password = PasswordGenerator.Generate();
            var stored = _passwords.Find(vmId, userId);
            var change = new PasswordChange(false, null);
            try
            {
                if (stored is null)
                {
                    await _guest.SetPasswordAsync(target, admin, accountName, password, cancellationToken);

                }
                else
                {
                    change = await _guest.ChangePasswordAsync(target, admin, accountName, stored, password, cancellationToken);
                }
            }
            catch (GuestCredentialRejectedException ex)
            {
                var reason = GuestErrors.Clean(ex.Message, admin.Password, password, stored ?? password);
                _status?.RecordRejected(vmId, reason, now);
                if (stored is null)
                {
                    throw new GuestCredentialRejectedException(reason);
                }

                // Nothing was rotated, so the password set last time still signs the User in.
                _logger.LogWarning(
                    "The guest rejected the stored administrator credential for VM {VmId} ({Reason}). Issued the password set at the last connect for {Account} without rotating it.",
                    vmId,
                    reason,
                    accountName);
                return new RotatedPassword(stored, now, Rotated: false);
            }
            catch (Exception ex) when (GuestErrors.IsGuestError(ex))
            {
                throw GuestErrors.Sanitize(ex, admin.Password, password, stored ?? password);
            }

            _passwords.Save(vmId, userId, password);
            _status?.RecordWorking(vmId, change.Administrator, now);
            if (admin.Managed && change.Administrator is { PasswordExpiresAt: { } expires } administrator)
            {
                _logger.LogWarning(
                    "Password expiry could not be turned off for the administrator account {Admin} on VM {VmId} ({Reason}); its password expires {Expires:u}.",
                    admin.UserName,
                    vmId,
                    GuestErrors.Clean(administrator.Problem ?? "the guest did not say why", admin.Password, password, stored ?? password),
                    expires);
            }

            if (stored is not null && !change.Changed)
            {
                _logger.LogWarning(
                    "The password for {Account} on VM {VmId} could not be changed ({Reason}), so it was reset; data the account protected with Windows may no longer open.",
                    accountName,
                    vmId,
                    GuestErrors.Clean(change.ResetReason ?? "", admin.Password, password, stored));
            }

            entry.Clear();
            entry.Password = password.ToCharArray();
            entry.ExpiresAt = now + _reuseWindow;
            entry.ClearTimer = _time.CreateTimer(_ => ClearIfExpired(entry), null, _reuseWindow, Timeout.InfiniteTimeSpan);

            _logger.LogInformation("Rotated the password for {Account} on VM {VmId}.", accountName, vmId);
            return new RotatedPassword(password, entry.ExpiresAt);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>True while a password for this VM and User is cached. Exposed for tests.</summary>
    internal bool HasCachedPassword(Guid vmId, Guid userId) =>
        _entries.TryGetValue((vmId, userId), out var entry) && entry.Password is not null;

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Clear();
        }
    }

    private void ClearIfExpired(Entry entry)
    {
        entry.Gate.Wait();
        try
        {
            if (_time.GetUtcNow() >= entry.ExpiresAt)
            {
                entry.Clear();
            }
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public char[]? Password { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public ITimer? ClearTimer { get; set; }

        public void Clear()
        {
            if (Password is not null)
            {
                Array.Clear(Password);
                Password = null;
            }

            ClearTimer?.Dispose();
            ClearTimer = null;
        }
    }
}
