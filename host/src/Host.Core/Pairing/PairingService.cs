using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts.Pairing;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Pairing;

/// <summary>
/// Host side of the SPAKE2 pairing protocol (docs/pairing.md). Holds at most one pending
/// request. The PIN and SPAKE2 secrets are kept in memory only and never logged.
/// </summary>
public sealed class PairingService : IDisposable
{
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromSeconds(120);
    public const int MaxAttempts = 5;
    public const int MaxDeviceNameLength = 64;

    private readonly HostCertificateStore _hostCertificate;
    private readonly PairedDeviceStore _devices;
    private readonly HostIdentityStore _identity;
    private readonly IPairingNotifier _notifier;
    private readonly TimeProvider _time;
    private readonly ILogger<PairingService> _logger;
    private readonly object _gate = new();

    private PendingRequest? _pending;
    private ITimer? _expiryTimer;
    private Guid? _lastEndedId;

    public PairingService(
        HostCertificateStore hostCertificate,
        PairedDeviceStore devices,
        HostIdentityStore identity,
        IPairingNotifier notifier,
        TimeProvider time,
        ILogger<PairingService> logger)
    {
        _hostCertificate = hostCertificate;
        _devices = devices;
        _identity = identity;
        _notifier = notifier;
        _time = time;
        _logger = logger;
    }

    public PairingRequestCreated CreateRequest(PairingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var deviceName = NormalizeDeviceName(request.DeviceName);
        var clientCertificateHash = ParseClientCertificateHash(request.ClientCertificatePem);

        if (!_notifier.CanDisplayPin)
        {
            throw new PairingException(PairingError.NoDisplay, "Open the HyperHarbor tray app on the host to pair this device.");
        }

        PendingRequest pending;
        lock (_gate)
        {
            ExpireIfDue();
            if (_pending is not null)
            {
                throw new PairingException(PairingError.RequestPending, "Another pairing request is in progress. Try again when it finishes.");
            }

            var pairingId = Guid.NewGuid();
            var pin = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
            var w = Spake2.PasswordScalar(pairingId, pin);
            var y = Spake2.RandomScalar();

            pending = new PendingRequest(
                pairingId,
                deviceName,
                clientCertificateHash,
                pin,
                w,
                y,
                Spake2.ComputeShare(Spake2.Role.Host, y, w),
                _time.GetUtcNow() + RequestLifetime);
            _pending = pending;

            _expiryTimer?.Dispose();
            _expiryTimer = _time.CreateTimer(_ => OnExpiryTimer(pairingId), null, RequestLifetime, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Pairing requested by {DeviceName} ({PairingId}).", deviceName, pending.PairingId);
        _notifier.PairingStarted(pending.PairingId, deviceName, pending.Pin, pending.ExpiresAt);

        return new PairingRequestCreated(pending.PairingId, Spake2.Encode(pending.HostShare), pending.ExpiresAt);
    }

    public PairingResult Confirm(Guid pairingId, PairingConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);

        PendingRequest pending;
        PairedDevice device;
        byte[] hostConfirmation;
        lock (_gate)
        {
            ExpireIfDue();
            if (_pending is null || _pending.PairingId != pairingId)
            {
                throw pairingId == _lastEndedId
                    ? new PairingException(PairingError.Gone, "The pairing request expired or reached the attempt limit. Start pairing again.")
                    : new PairingException(PairingError.NotFound, "No pairing request has this ID.");
            }

            pending = _pending;
            if (Spake2.TryDecodeShare(confirmation.ClientShare) is not { } clientShare)
            {
                throw new PairingException(PairingError.InvalidRequest, "The client share is not a valid group element.");
            }

            var sharedElement = Spake2.ComputeSharedElement(Spake2.Role.Host, pending.Y, pending.W, clientShare);
            var expected = Spake2.ComputeConfirmations(
                pairingId,
                pending.ClientCertificateHash,
                SHA256.HashData(_hostCertificate.GetOrCreate().RawData),
                clientShare,
                pending.HostShare,
                sharedElement,
                pending.W);

            if (!CryptographicOperations.FixedTimeEquals(expected.Client, confirmation.ClientConfirmation))
            {
                pending.Attempts++;
                _logger.LogWarning(
                    "Pairing confirmation from {DeviceName} did not match (attempt {Attempt} of {Max}).",
                    pending.DeviceName,
                    pending.Attempts,
                    MaxAttempts);

                if (pending.Attempts >= MaxAttempts)
                {
                    EndPending(PairingOutcome.TooManyAttempts);
                    throw new PairingException(PairingError.Gone, "Too many incorrect PIN attempts. Start pairing again.");
                }

                throw new PairingException(
                    PairingError.ConfirmationMismatch,
                    $"The PIN is incorrect. {MaxAttempts - pending.Attempts} attempt(s) remaining.");
            }

            device = _devices.Add(pending.DeviceName, Convert.ToHexString(pending.ClientCertificateHash), _time.GetUtcNow());
            hostConfirmation = expected.Host;
            EndPending(PairingOutcome.Paired);
        }

        _logger.LogInformation("Paired device {DeviceName} ({DeviceId}).", device.Name, device.DeviceId);
        return new PairingResult(
            device.DeviceId,
            _identity.GetOrCreateHostId(),
            _hostCertificate.GetOrCreate().ExportCertificatePem(),
            hostConfirmation);
    }

    /// <summary>Cancels the pending request, for example from the tray.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                EndPending(PairingOutcome.Cancelled);
            }
        }
    }

    public void Dispose()
    {
        _expiryTimer?.Dispose();
    }

    private void OnExpiryTimer(Guid pairingId)
    {
        lock (_gate)
        {
            if (_pending?.PairingId == pairingId)
            {
                EndPending(PairingOutcome.Expired);
            }
        }
    }

    private void ExpireIfDue()
    {
        if (_pending is not null && _time.GetUtcNow() >= _pending.ExpiresAt)
        {
            EndPending(PairingOutcome.Expired);
        }
    }

    /// <remarks>Caller holds <see cref="_gate"/>.</remarks>
    private void EndPending(PairingOutcome outcome)
    {
        var ended = _pending!;
        _pending = null;
        _lastEndedId = ended.PairingId;
        _expiryTimer?.Dispose();
        _expiryTimer = null;

        if (outcome != PairingOutcome.Paired)
        {
            _logger.LogInformation("Pairing with {DeviceName} ended: {Outcome}.", ended.DeviceName, outcome);
        }

        _notifier.PairingEnded(ended.PairingId, ended.DeviceName, outcome);
    }

    private static string NormalizeDeviceName(string? name)
    {
        var cleaned = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length is 0 or > MaxDeviceNameLength)
        {
            throw new PairingException(PairingError.InvalidRequest, $"The device name must be 1 to {MaxDeviceNameLength} characters.");
        }

        return cleaned;
    }

    private byte[] ParseClientCertificateHash(string? pem)
    {
        try
        {
            using var certificate = X509Certificate2.CreateFromPem(pem);
            var now = _time.GetUtcNow();
            if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
            {
                throw new PairingException(PairingError.InvalidRequest, "The client certificate is not currently valid.");
            }

            return SHA256.HashData(certificate.RawData);
        }
        catch (CryptographicException)
        {
            throw new PairingException(PairingError.InvalidRequest, "The client certificate could not be parsed.");
        }
        catch (ArgumentException)
        {
            throw new PairingException(PairingError.InvalidRequest, "The client certificate could not be parsed.");
        }
    }

    private sealed class PendingRequest(
        Guid pairingId,
        string deviceName,
        byte[] clientCertificateHash,
        string pin,
        BigInteger w,
        BigInteger y,
        BigInteger hostShare,
        DateTimeOffset expiresAt)
    {
        public Guid PairingId { get; } = pairingId;
        public string DeviceName { get; } = deviceName;
        public byte[] ClientCertificateHash { get; } = clientCertificateHash;
        public string Pin { get; } = pin;
        public BigInteger W { get; } = w;
        public BigInteger Y { get; } = y;
        public BigInteger HostShare { get; } = hostShare;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public int Attempts { get; set; }
    }
}
