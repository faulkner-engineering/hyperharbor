using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Identity;
using HyperHarbor.Host.Core.Pairing;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Pairing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Pairing;

public sealed class PairingServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeTray _tray = new();
    private readonly X509Certificate2 _clientCertificate = TestHost.CreateClientCertificate();
    private readonly HostCertificateStore _hostCertificate;
    private readonly PairedDeviceStore _devices;
    private readonly UserStore _users;
    private readonly PairingService _service;

    public PairingServiceTests()
    {
        _hostCertificate = new HostCertificateStore(_directory, "TEST-HOST");
        _users = new UserStore(_directory);
        _devices = new PairedDeviceStore(_directory, _users);
        _service = new PairingService(
            _hostCertificate,
            _devices,
            new HostIdentityStore(_directory),
            _users,
            _tray,
            _time,
            NullLogger<PairingService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _clientCertificate.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void CreateRequest_GeneratesSixDigitPin_AndValidShare()
    {
        var created = _service.CreateRequest(Request());

        Assert.Matches("^[0-9]{6}$", _tray.Pin);
        Assert.NotNull(Spake2.TryDecodeShare(created.HostShare));
        Assert.Equal(_time.GetUtcNow() + PairingService.RequestLifetime, created.ExpiresAt);
    }

    [Fact]
    public void Confirm_WithCorrectPin_StoresDeviceAndNotifies()
    {
        var created = _service.CreateRequest(Request());

        var result = _service.Confirm(created.PairingId, Exchange(created, _tray.Pin!).Confirmation);

        var device = Assert.Single(_devices.List());
        Assert.Equal(result.DeviceId, device.DeviceId);
        Assert.Equal(_users.GetOrCreateDefault().UserId, device.UserId);
        Assert.Equal(device.UserId, result.UserId);
        Assert.Equal("Laptop", device.Name);
        Assert.Equal(CertificateFingerprint.Of(_clientCertificate), device.CertificateFingerprint);
        Assert.Equal([PairingOutcome.Paired], _tray.Outcomes);
    }

    [Fact]
    public void Confirm_InvalidShare_DoesNotCountAsAttempt()
    {
        var created = _service.CreateRequest(Request());
        var invalid = new PairingConfirmation(Spake2.Encode(1), new byte[32]);

        for (var i = 0; i < PairingService.MaxAttempts + 1; i++)
        {
            var error = Assert.Throws<PairingException>(() => _service.Confirm(created.PairingId, invalid));
            Assert.Equal(PairingError.InvalidRequest, error.Error);
        }

        _service.Confirm(created.PairingId, Exchange(created, _tray.Pin!).Confirmation);
    }

    [Fact]
    public void Confirm_AfterExpiry_IsGone()
    {
        var created = _service.CreateRequest(Request());
        _time.Advance(PairingService.RequestLifetime);

        var error = Assert.Throws<PairingException>(() => _service.Confirm(created.PairingId, Exchange(created, _tray.Pin!).Confirmation));

        Assert.Equal(PairingError.Gone, error.Error);
        Assert.Equal([PairingOutcome.Expired], _tray.Outcomes);
        Assert.Empty(_devices.List());
    }

    [Fact]
    public void Confirm_CertificateSubstitution_IsRejected()
    {
        var created = _service.CreateRequest(Request());
        using var attacker = TestHost.CreateClientCertificate("Attacker");

        // Correct PIN, but computed over a different client certificate than the one submitted.
        var exchange = TestPairingClient.Compute(created.PairingId, _tray.Pin!, created.HostShare, attacker, _hostCertificate.GetOrCreate().RawData);
        var error = Assert.Throws<PairingException>(() => _service.Confirm(created.PairingId, exchange.Confirmation));

        Assert.Equal(PairingError.ConfirmationMismatch, error.Error);
    }

    [Fact]
    public void ExpiryTimer_NotifiesTray_AndAllowsNewRequest()
    {
        _service.CreateRequest(Request());

        _time.Advance(PairingService.RequestLifetime);

        Assert.Equal([PairingOutcome.Expired], _tray.Outcomes);
        _service.CreateRequest(Request());
    }

    [Fact]
    public void Cancel_EndsPendingRequest()
    {
        var created = _service.CreateRequest(Request());

        _service.Cancel();

        Assert.Equal([PairingOutcome.Cancelled], _tray.Outcomes);
        Assert.Equal(PairingError.Gone, Assert.Throws<PairingException>(() => _service.Confirm(created.PairingId, Exchange(created, _tray.Pin!).Confirmation)).Error);
    }

    [Fact]
    public void CreateRequest_ExpiredClientCertificate_IsRejected()
    {
        _time.Advance(TimeSpan.FromDays(400));

        var error = Assert.Throws<PairingException>(() => _service.CreateRequest(Request()));

        Assert.Equal(PairingError.InvalidRequest, error.Error);
    }

    [Fact]
    public void CreateRequest_StripsControlCharactersFromName()
    {
        var created = _service.CreateRequest(new PairingRequest("  Lap\u0000top\n ", _clientCertificate.ExportCertificatePem()));

        _service.Confirm(created.PairingId, Exchange(created, _tray.Pin!).Confirmation);

        Assert.Equal("Laptop", Assert.Single(_devices.List()).Name);
    }

    private PairingRequest Request() => new("Laptop", _clientCertificate.ExportCertificatePem());

    private TestPairingClient.Exchange Exchange(PairingRequestCreated created, string pin) =>
        TestPairingClient.Compute(created.PairingId, pin, created.HostShare, _clientCertificate, _hostCertificate.GetOrCreate().RawData);
}
