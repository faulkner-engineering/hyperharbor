using System.Security.Cryptography;
using System.Text;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>A console ticket as issued. ToString never includes the value.</summary>
public sealed record ConsoleTicket(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"ConsoleTicket {{ ExpiresAt = {ExpiresAt:u} }}";
}

/// <summary>
/// Tickets that let a paired device open console tunnels to one VM. A ticket is random, kept only as
/// its SHA-256 hash in memory, bound to the device, User, and VM it was issued for, and valid for
/// several tunnels until it expires, because Remote Desktop clients may reconnect. Tickets end when
/// their device is unpaired or the service restarts.
/// </summary>
public sealed class ConsoleTicketStore
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(120);

    private readonly TimeProvider _time;
    private readonly TimeSpan _lifetime;
    private readonly PairedDeviceStore? _devices;
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);

    public ConsoleTicketStore(TimeProvider time, TimeSpan lifetime, PairedDeviceStore? devices = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        _time = time;
        _lifetime = lifetime;
        _devices = devices;
        if (devices is not null)
        {
            devices.Changed += (_, _) => RevokeRemovedDevices();
        }
    }

    public ConsoleTicket Issue(Guid deviceId, Guid userId, Guid vmId)
    {
        var value = Base64Url(RandomNumberGenerator.GetBytes(32));
        var expiresAt = _time.GetUtcNow() + _lifetime;
        lock (_gate)
        {
            RemoveExpired();
            _grants[Hash(value)] = new Grant(deviceId, userId, vmId, expiresAt);
        }

        return new ConsoleTicket(value, expiresAt);
    }

    /// <exception cref="ConsoleTicketRejectedException">The ticket is unknown, expired, or bound to something else.</exception>
    public void Validate(string? ticket, Guid deviceId, Guid userId, Guid vmId)
    {
        if (string.IsNullOrEmpty(ticket) || ticket.Length > 64)
        {
            throw new ConsoleTicketRejectedException();
        }

        lock (_gate)
        {
            RemoveExpired();
            if (!_grants.TryGetValue(Hash(ticket), out var grant)
                || grant.DeviceId != deviceId
                || grant.UserId != userId
                || grant.VmId != vmId)
            {
                throw new ConsoleTicketRejectedException();
            }
        }
    }

    /// <summary>The number of live tickets. Exposed for tests.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                RemoveExpired();
                return _grants.Count;
            }
        }
    }

    private void RemoveExpired()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _grants.Where(grant => grant.Value.ExpiresAt <= now).Select(grant => grant.Key).ToList())
        {
            _grants.Remove(key);
        }
    }

    private void RevokeRemovedDevices()
    {
        var paired = _devices!.List().Select(device => device.DeviceId).ToHashSet();
        lock (_gate)
        {
            foreach (var key in _grants.Where(grant => !paired.Contains(grant.Value.DeviceId)).Select(grant => grant.Key).ToList())
            {
                _grants.Remove(key);
            }
        }
    }

    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed record Grant(Guid DeviceId, Guid UserId, Guid VmId, DateTimeOffset ExpiresAt);
}
