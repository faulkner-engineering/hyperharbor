using HyperHarbor.Host.Core.Provisioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class PasswordRotatorTests : IDisposable
{
    private static readonly Guid VmId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeGuestAccountManager _guest = new();
    private readonly VmCredentialStore _credentials;
    private readonly PasswordRotator _rotator;

    public PasswordRotatorTests()
    {
        _credentials = new VmCredentialStore(_directory);
        _credentials.Save(VmId, new GuestCredential("Administrator", "Adm1n!"));
        _rotator = new PasswordRotator(_guest, _credentials, _time, TimeSpan.FromSeconds(60), NullLogger<PasswordRotator>.Instance);
    }

    public void Dispose()
    {
        _rotator.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task FirstRequest_RotatesInGuest()
    {
        var result = await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);

        var (vmId, account, password) = Assert.Single(_guest.PasswordsSet);
        Assert.Equal((VmId, "hh-owner", result.Password), (vmId, account, password));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(60), result.ExpiresAt);
        Assert.Equal(new GuestCredential("Administrator", "Adm1n!"), Assert.Single(_guest.AdminCredentialsUsed));
    }

    [Fact]
    public async Task RequestsWithinWindow_ReuseThePassword()
    {
        var first = await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(59));

        var second = await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);

        Assert.Equal(first.Password, second.Password);
        Assert.Single(_guest.PasswordsSet);
    }

    [Fact]
    public async Task RequestAfterWindow_RotatesAgain_AndCacheIsCleared()
    {
        var first = await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);
        Assert.True(_rotator.HasCachedPassword(VmId, UserId));

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(_rotator.HasCachedPassword(VmId, UserId));

        var second = await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);
        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(2, _guest.PasswordsSet.Count);
    }

    [Fact]
    public async Task ConcurrentRequests_RotateOnce()
    {
        _guest.Delay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None)));

        Assert.Single(_guest.PasswordsSet);
        Assert.Single(results.Select(r => r.Password).Distinct());
    }

    [Fact]
    public async Task DifferentUsers_RotateIndependently()
    {
        await _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None);
        await _rotator.GetPasswordAsync(VmId, Guid.NewGuid(), "hh-other", CancellationToken.None);

        Assert.Equal(["hh-owner", "hh-other"], _guest.PasswordsSet.Select(p => p.Account));
    }

    [Fact]
    public async Task FailedRotation_IsNotCached()
    {
        _guest.Failure = new GuestUnavailableException("starting");
        await Assert.ThrowsAsync<GuestUnavailableException>(() => _rotator.GetPasswordAsync(VmId, UserId, "hh-owner", CancellationToken.None));

        Assert.False(_rotator.HasCachedPassword(VmId, UserId));
    }

    [Fact]
    public async Task MissingAdminCredential_IsConflict()
    {
        await Assert.ThrowsAsync<GuestAccountConflictException>(() => _rotator.GetPasswordAsync(Guid.NewGuid(), UserId, "hh-owner", CancellationToken.None));
    }

    [Fact]
    public void RotatedPassword_HidesPasswordInToString()
    {
        Assert.DoesNotContain("Secret-123", new RotatedPassword("Secret-123", DateTimeOffset.UtcNow).ToString());
    }
}
