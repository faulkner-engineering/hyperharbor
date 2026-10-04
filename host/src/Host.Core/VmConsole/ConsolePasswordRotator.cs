using System.Collections.Concurrent;
using HyperHarbor.Host.Core.Provisioning;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>
/// Rotates a User's host console account password for each console request. Requests for the same
/// User are serialized, and requests within the reuse window get the password that was just set, so
/// devices opening consoles together do not invalidate each other. When the window ends the password
/// is rotated again, so a delivered password stops working soon after its session has started
/// (sessions already signed in are not affected). The delivered password is held in memory only, as
/// a char array that is zeroed when the window ends; the account's current password is kept only in
/// the DPAPI-protected <see cref="ConsoleAccountStore"/>.
/// </summary>
public sealed class ConsolePasswordRotator : IDisposable
{
    public static readonly TimeSpan DefaultReuseWindow = TimeSpan.FromSeconds(60);

    private readonly ConsoleAccountStore _store;
    private readonly IConsolePasswordChanger _changer;
    private readonly TimeProvider _time;
    private readonly TimeSpan _reuseWindow;
    private readonly ILogger<ConsolePasswordRotator> _logger;
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public ConsolePasswordRotator(
        ConsoleAccountStore store,
        IConsolePasswordChanger changer,
        TimeProvider time,
        TimeSpan reuseWindow,
        ILogger<ConsolePasswordRotator> logger)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(reuseWindow, TimeSpan.Zero);
        _store = store;
        _changer = changer;
        _time = time;
        _reuseWindow = reuseWindow;
        _logger = logger;
    }

    /// <exception cref="ConsoleConflictException">The User has no console account, or its stored password is out of date.</exception>
    public async Task<RotatedPassword> GetPasswordAsync(Guid userId, CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(userId, _ => new Entry());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            if (entry.Password is { } current && now < entry.ExpiresAt)
            {
                return new RotatedPassword(new string(current), entry.ExpiresAt);
            }

            var password = Rotate(userId);
            entry.Clear();
            entry.Password = password.ToCharArray();
            entry.ExpiresAt = now + _reuseWindow;
            entry.Timer = _time.CreateTimer(_ => _ = EndWindowAsync(userId, entry), null, _reuseWindow, Timeout.InfiniteTimeSpan);
            return new RotatedPassword(password, entry.ExpiresAt);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>True while a delivered password for this User is cached. Exposed for tests.</summary>
    internal bool HasCachedPassword(Guid userId) =>
        _entries.TryGetValue(userId, out var entry) && entry.Password is not null;

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Clear();
        }
    }

    private string Rotate(Guid userId)
    {
        var credential = _store.Find(userId)
            ?? throw ConsoleConflictException.SetupRequired("This host has no console account for your user yet.");
        var password = PasswordGenerator.Generate();
        _changer.ChangePassword(credential.AccountName, credential.Password, password);
        _store.Save(userId, credential with { Password = password });
        _logger.LogInformation("Rotated the password of the host console account {Account}.", credential.AccountName);
        return password;
    }

    /// <summary>Forgets the delivered password and rotates again, unless a newer window has started.</summary>
    private async Task EndWindowAsync(Guid userId, Entry entry)
    {
        await entry.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (entry.Password is null || _time.GetUtcNow() < entry.ExpiresAt)
            {
                return;
            }

            entry.Clear();
            Rotate(userId);
        }
        catch (Exception ex)
        {
            // The delivered password stays valid until the next successful rotation.
            _logger.LogWarning(ex, "Could not rotate the host console account password after the reuse window.");
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

        public ITimer? Timer { get; set; }

        public void Clear()
        {
            if (Password is not null)
            {
                Array.Clear(Password);
                Password = null;
            }

            Timer?.Dispose();
            Timer = null;
        }
    }
}
