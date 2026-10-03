using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Elevation;

public sealed class ElevationServiceTests : IDisposable
{
    /// <summary>Few iterations keep the tests fast; the tray and pipe use <see cref="AdminPassphrase.DefaultIterations"/>.</summary>
    internal const int TestIterations = 1_000;

    private const string Passphrase = "correct horse battery";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly PairedDeviceStore _devices;
    private readonly AdminPassphraseStore _store;
    private readonly ElevationService _elevation;
    private readonly Guid _userId;
    private readonly Guid _deviceId;

    public ElevationServiceTests()
    {
        var users = new UserStore(_directory);
        _userId = users.GetOrCreateDefault().UserId;
        _devices = new PairedDeviceStore(_directory, users);
        _deviceId = _devices.Add(_userId, "Laptop", new string('A', 64), _time.GetUtcNow()).DeviceId;
        _store = new AdminPassphraseStore(_directory);
        _elevation = new ElevationService(_store, _devices, _time, NullLogger<ElevationService>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Elevate_WithoutPassphrase_IsUnavailable()
    {
        Assert.False(_elevation.IsConfigured);

        await Assert.ThrowsAsync<ElevationUnavailableException>(() => ElevateAsync(_deviceId, Passphrase));
    }

    [Fact]
    public async Task Elevate_WithTheRightPassphrase_GrantsATokenForThatDeviceAndUserOnly()
    {
        SetPassphrase();

        var grant = await ElevateAsync(_deviceId, Passphrase);

        Assert.Equal(_time.GetUtcNow() + ElevationService.DefaultTokenLifetime, grant.ExpiresAt);
        Assert.True(_elevation.IsElevated(_deviceId, _userId, grant.Token));
        Assert.False(_elevation.IsElevated(Guid.NewGuid(), _userId, grant.Token));
        Assert.False(_elevation.IsElevated(_deviceId, Guid.NewGuid(), grant.Token));
        Assert.False(_elevation.IsElevated(_deviceId, _userId, null));
        Assert.False(_elevation.IsElevated(_deviceId, _userId, string.Empty));
        Assert.False(_elevation.IsElevated(_deviceId, _userId, "not a token"));
        Assert.False(_elevation.IsElevated(_deviceId, _userId, grant.Token[..^2] + (grant.Token[^2] == 'A' ? "BA" : "AA")));
        Assert.DoesNotContain(grant.Token, grant.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Token_ExpiresAfterItsLifetime()
    {
        SetPassphrase();
        var grant = await ElevateAsync(_deviceId, Passphrase);

        _time.Advance(ElevationService.DefaultTokenLifetime - TimeSpan.FromSeconds(1));
        Assert.True(_elevation.IsElevated(_deviceId, _userId, grant.Token));
        Assert.True(_elevation.Status(_deviceId).Active);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_elevation.IsElevated(_deviceId, _userId, grant.Token));
        var status = _elevation.Status(_deviceId);
        Assert.False(status.Active);
        Assert.Null(status.ExpiresAt);
    }

    [Fact]
    public async Task NewElevation_ReplacesTheDevicesEarlierToken()
    {
        SetPassphrase();
        var first = await ElevateAsync(_deviceId, Passphrase);

        var second = await ElevateAsync(_deviceId, Passphrase);

        Assert.False(_elevation.IsElevated(_deviceId, _userId, first.Token));
        Assert.True(_elevation.IsElevated(_deviceId, _userId, second.Token));
    }

    [Fact]
    public async Task Drop_EndsElevation()
    {
        SetPassphrase();
        var grant = await ElevateAsync(_deviceId, Passphrase);

        _elevation.Drop(_deviceId);

        Assert.False(_elevation.IsElevated(_deviceId, _userId, grant.Token));
    }

    [Fact]
    public async Task ChangingThePassphrase_EndsEveryElevation()
    {
        SetPassphrase();
        var grant = await ElevateAsync(_deviceId, Passphrase);

        SetPassphrase("a different passphrase");

        Assert.False(_elevation.IsElevated(_deviceId, _userId, grant.Token));
        await Assert.ThrowsAsync<IncorrectPassphraseException>(() => ElevateAsync(_deviceId, Passphrase));
    }

    [Fact]
    public async Task UnpairingTheDevice_EndsItsElevation()
    {
        SetPassphrase();
        var grant = await ElevateAsync(_deviceId, Passphrase);

        _devices.Remove(_deviceId);

        Assert.False(_elevation.IsElevated(_deviceId, _userId, grant.Token));
    }

    [Fact]
    public async Task WrongPassphrases_AreLimitedPerDevice_EvenForTheRightOneAfterwards()
    {
        SetPassphrase();
        for (var attempt = 0; attempt < ElevationService.MaxFailuresPerDevice; attempt++)
        {
            await Assert.ThrowsAsync<IncorrectPassphraseException>(() => ElevateAsync(_deviceId, "wrong passphrase"));
        }

        var limited = await Assert.ThrowsAsync<ElevationRateLimitedException>(() => ElevateAsync(_deviceId, Passphrase));
        Assert.Equal(ElevationService.DeviceFailureWindow, limited.RetryAfter);

        // Another device is not affected by this one's failures.
        Assert.NotNull(await ElevateAsync(Guid.NewGuid(), Passphrase));

        _time.Advance(ElevationService.DeviceFailureWindow);
        Assert.NotNull(await ElevateAsync(_deviceId, Passphrase));
    }

    [Fact]
    public async Task WrongPassphrases_AreLimitedAcrossAllDevices()
    {
        SetPassphrase();
        for (var attempt = 0; attempt < ElevationService.MaxFailuresOverall; attempt++)
        {
            // Spread across devices so no single device reaches its own limit.
            await Assert.ThrowsAsync<IncorrectPassphraseException>(() => ElevateAsync(Guid.NewGuid(), "wrong passphrase"));
        }

        await Assert.ThrowsAsync<ElevationRateLimitedException>(() => ElevateAsync(Guid.NewGuid(), Passphrase));

        _time.Advance(ElevationService.OverallFailureWindow);
        Assert.NotNull(await ElevateAsync(Guid.NewGuid(), Passphrase));
    }

    [Fact]
    public async Task Success_ClearsTheDevicesFailures()
    {
        SetPassphrase();
        for (var attempt = 0; attempt < ElevationService.MaxFailuresPerDevice - 1; attempt++)
        {
            await Assert.ThrowsAsync<IncorrectPassphraseException>(() => ElevateAsync(_deviceId, "wrong passphrase"));
        }

        await ElevateAsync(_deviceId, Passphrase);

        await Assert.ThrowsAsync<IncorrectPassphraseException>(() => ElevateAsync(_deviceId, "wrong passphrase"));
        Assert.NotNull(await ElevateAsync(_deviceId, Passphrase));
    }

    [Fact]
    public void Status_ReportsWhetherAPassphraseIsSet()
    {
        Assert.Equal(new(false, false, null), _elevation.Status(_deviceId));

        SetPassphrase();

        Assert.True(_elevation.Status(_deviceId).Configured);
    }

    private void SetPassphrase(string passphrase = Passphrase)
    {
        var hash = AdminPassphrase.CreateHash(passphrase, TestIterations);
        _elevation.SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    private Task<Shared.Contracts.Auth.ElevationGrant> ElevateAsync(Guid deviceId, string passphrase) =>
        _elevation.ElevateAsync(deviceId, _userId, passphrase, CancellationToken.None);
}
