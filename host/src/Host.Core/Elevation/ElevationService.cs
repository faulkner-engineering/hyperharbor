using System.Security.Cryptography;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Auth;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Elevation;

/// <summary>
/// Sudo-style elevation. A paired device proves knowledge of the admin passphrase and receives a
/// random token that is valid for <see cref="TokenLifetime"/> and only for that device and User.
/// Only the SHA-256 of each token is kept, in memory; a service restart, a passphrase change, or
/// unpairing the device ends it.
/// Wrong passphrases are limited per device and across all devices, and each attempt is checked one
/// at a time so parallel guesses cannot race the limits.
/// </summary>
public sealed class ElevationService
{
    public static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(5);
    public const int MaxFailuresPerDevice = 5;
    public static readonly TimeSpan DeviceFailureWindow = TimeSpan.FromMinutes(15);
    public const int MaxFailuresOverall = 20;
    public static readonly TimeSpan OverallFailureWindow = TimeSpan.FromHours(1);
    private const int TokenBytes = 32;

    private readonly AdminPassphraseStore _passphrase;
    private readonly PairedDeviceStore _devices;
    private readonly TimeProvider _time;
    private readonly ILogger<ElevationService> _logger;
    private readonly SemaphoreSlim _attempts = new(1, 1);
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Grant> _grants = [];
    private readonly Dictionary<Guid, List<DateTimeOffset>> _deviceFailures = [];
    private readonly List<DateTimeOffset> _allFailures = [];

    public ElevationService(
        AdminPassphraseStore passphrase,
        PairedDeviceStore devices,
        TimeProvider time,
        ILogger<ElevationService> logger,
        TimeSpan? tokenLifetime = null)
    {
        _passphrase = passphrase;
        _devices = devices;
        _time = time;
        _logger = logger;
        TokenLifetime = tokenLifetime ?? DefaultTokenLifetime;
        _devices.Changed += (_, _) => RevokeRemovedDevices();
    }

    public TimeSpan TokenLifetime { get; }

    public bool IsConfigured => _passphrase.IsConfigured;

    /// <exception cref="ElevationUnavailableException">No admin passphrase is set.</exception>
    /// <exception cref="ElevationRateLimitedException">Too many recent wrong passphrases.</exception>
    /// <exception cref="IncorrectPassphraseException">The passphrase is wrong.</exception>
    public async Task<ElevationGrant> ElevateAsync(Guid deviceId, Guid userId, string passphrase, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(passphrase);
        if (!_passphrase.IsConfigured)
        {
            throw new ElevationUnavailableException();
        }

        await _attempts.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfRateLimited(deviceId);

            // PBKDF2 is deliberately slow; it runs outside _gate so token checks are not delayed.
            if (!_passphrase.Verify(passphrase))
            {
                RecordFailure(deviceId);
                _logger.LogWarning("Incorrect admin passphrase from device {DeviceId}.", deviceId);
                throw new IncorrectPassphraseException();
            }

            var token = RandomNumberGenerator.GetBytes(TokenBytes);
            var expiresAt = _time.GetUtcNow() + TokenLifetime;
            lock (_gate)
            {
                _deviceFailures.Remove(deviceId);
                _grants[deviceId] = new Grant(SHA256.HashData(token), userId, expiresAt);
            }

            _logger.LogInformation("Device {DeviceId} elevated until {ExpiresAt:u}.", deviceId, expiresAt);
            return new ElevationGrant(Base64Url(token), expiresAt);
        }
        finally
        {
            _attempts.Release();
        }
    }

    /// <summary>True when <paramref name="token"/> is the unexpired token issued to this device and User.</summary>
    public bool IsElevated(Guid deviceId, Guid userId, string? token)
    {
        if (string.IsNullOrEmpty(token) || FromBase64Url(token) is not { } raw)
        {
            return false;
        }

        var hash = SHA256.HashData(raw);
        lock (_gate)
        {
            return _grants.TryGetValue(deviceId, out var grant)
                && grant.UserId == userId
                && grant.ExpiresAt > _time.GetUtcNow()
                && CryptographicOperations.FixedTimeEquals(grant.TokenHash, hash);
        }
    }

    public ElevationStatus Status(Guid deviceId)
    {
        lock (_gate)
        {
            var active = _grants.TryGetValue(deviceId, out var grant) && grant.ExpiresAt > _time.GetUtcNow();
            return new ElevationStatus(_passphrase.IsConfigured, active, active ? grant!.ExpiresAt : null);
        }
    }

    /// <summary>Ends the device's elevation, if any.</summary>
    public void Drop(Guid deviceId)
    {
        lock (_gate)
        {
            _grants.Remove(deviceId);
        }
    }

    /// <summary>Stores a new passphrase hash from the tray and ends every elevation.</summary>
    /// <exception cref="ArgumentException">The hash is not in the expected format.</exception>
    public void SetPassphrase(byte[] salt, byte[] hash, int iterations)
    {
        _passphrase.Set(salt, hash, iterations);
        lock (_gate)
        {
            _grants.Clear();
            _deviceFailures.Clear();
            _allFailures.Clear();
        }

        _logger.LogInformation("The admin passphrase was changed; all elevations ended.");
    }

    private void ThrowIfRateLimited(Guid deviceId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _allFailures.RemoveAll(time => time <= now - OverallFailureWindow);
            if (_allFailures.Count >= MaxFailuresOverall)
            {
                throw new ElevationRateLimitedException(_allFailures[0] + OverallFailureWindow - now);
            }

            if (_deviceFailures.TryGetValue(deviceId, out var failures))
            {
                failures.RemoveAll(time => time <= now - DeviceFailureWindow);
                if (failures.Count >= MaxFailuresPerDevice)
                {
                    throw new ElevationRateLimitedException(failures[0] + DeviceFailureWindow - now);
                }
            }
        }
    }

    private void RecordFailure(Guid deviceId)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _allFailures.Add(now);
            if (!_deviceFailures.TryGetValue(deviceId, out var failures))
            {
                _deviceFailures[deviceId] = failures = [];
            }

            failures.Add(now);
        }
    }

    private void RevokeRemovedDevices()
    {
        var paired = _devices.List().Select(device => device.DeviceId).ToHashSet();
        lock (_gate)
        {
            foreach (var deviceId in _grants.Keys.Where(id => !paired.Contains(id)).ToList())
            {
                _grants.Remove(deviceId);
            }
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string value)
    {
        if (value.Length > 64)
        {
            return null;
        }

        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        var buffer = new byte[TokenBytes];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written == TokenBytes ? buffer : null;
    }

    private sealed record Grant(byte[] TokenHash, Guid UserId, DateTimeOffset ExpiresAt);
}
