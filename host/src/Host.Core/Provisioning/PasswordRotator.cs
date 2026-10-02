using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <param name="ExpiresAt">End of the reuse window.</param>
public sealed record RotatedPassword(string Password, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"RotatedPassword {{ ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>
/// Rotates a User's VM account password on connect. Rotations for the same VM and User are
/// serialized, and callers within the reuse window get the password that was just set, so two
/// devices connecting together do not invalidate each other's credential. The password is held
/// only in memory, as a char array that is zeroed when the window ends; it is never logged.
/// </summary>
public sealed class PasswordRotator : IDisposable
{
    public static readonly TimeSpan DefaultReuseWindow = TimeSpan.FromSeconds(60);

    private readonly IGuestAccountManager _guest;
    private readonly VmCredentialStore _credentials;
    private readonly TimeProvider _time;
    private readonly TimeSpan _reuseWindow;
    private readonly ILogger<PasswordRotator> _logger;
    private readonly ConcurrentDictionary<(Guid VmId, Guid UserId), Entry> _entries = new();

    public PasswordRotator(
        IGuestAccountManager guest,
        VmCredentialStore credentials,
        TimeProvider time,
        TimeSpan reuseWindow,
        ILogger<PasswordRotator> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(reuseWindow, TimeSpan.Zero);
        _guest = guest;
        _credentials = credentials;
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
            await _guest.SetPasswordAsync(target, admin, accountName, password, cancellationToken);

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
