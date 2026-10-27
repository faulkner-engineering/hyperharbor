using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Provisioning;

public sealed class PasswordRotatorTests : IDisposable
{
    private static readonly Guid VmId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly GuestTarget Target = new(VmId, GuestOsFamily.Windows);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeGuestAccountManager _guest = new();
    private readonly VmCredentialStore _credentials;
    private readonly VmAccountPasswordStore _passwords;
    private readonly PasswordRotator _rotator;

    public PasswordRotatorTests()
    {
        _credentials = new VmCredentialStore(_directory);
        _passwords = new VmAccountPasswordStore(_directory);
        _credentials.Save(VmId, new GuestCredential("Administrator", "Adm1n!"));
        _rotator = new PasswordRotator(_guest, _credentials, _passwords, _time, TimeSpan.FromSeconds(60), NullLogger<PasswordRotator>.Instance);
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
        var result = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);

        var (vmId, account, password) = Assert.Single(_guest.PasswordsSet);
        Assert.Equal((VmId, "hh-owner", result.Password), (vmId, account, password));
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(60), result.ExpiresAt);
        Assert.Equal(new GuestCredential("Administrator", "Adm1n!"), Assert.Single(_guest.AdminCredentialsUsed));
    }

    [Fact]
    public async Task WithAStoredPassword_ChangesIt_AndStoresTheNewOne()
    {
        _passwords.Save(VmId, UserId, "previous-password");
        _guest.AcceptedCurrentPasswords.Add("previous-password");

        var result = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);

        Assert.Equal((VmId, "hh-owner", "previous-password", result.Password), Assert.Single(_guest.PasswordsChanged));
        Assert.Equal(result.Password, _passwords.Find(VmId, UserId));
    }

    [Fact]
    public async Task WithoutAStoredPassword_Resets_ThenLaterRotationsChange()
    {
        var first = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);
        Assert.Empty(_guest.PasswordsChanged);
        Assert.Equal(first.Password, _passwords.Find(VmId, UserId));
        _guest.AcceptedCurrentPasswords.Add(first.Password);

        _time.Advance(TimeSpan.FromSeconds(60));
        var second = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);

        Assert.Equal((VmId, "hh-owner", first.Password, second.Password), Assert.Single(_guest.PasswordsChanged));
    }

    [Fact]
    public async Task AStoredPasswordTheGuestRejects_IsReplacedByTheReset()
    {
        _passwords.Save(VmId, UserId, "stale-password");

        var result = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);

        Assert.Single(_guest.PasswordsChanged);
        Assert.Equal(result.Password, _passwords.Find(VmId, UserId));
    }

    [Fact]
    public void ThePasswordStore_KeepsEachUser_AndForgetsAVm()
    {
        var other = Guid.NewGuid();
        _passwords.Save(VmId, UserId, "one");
        _passwords.Save(VmId, other, "two");
        _passwords.Save(Guid.NewGuid(), UserId, "three");

        Assert.Equal("two", new VmAccountPasswordStore(_directory).Find(VmId, other));
        _passwords.RemoveVm(VmId);
        Assert.Null(_passwords.Find(VmId, UserId));
        Assert.Null(_passwords.Find(VmId, other));
        Assert.Contains("vm-account-passwords.json.protected", Directory.GetFiles(_directory).Select(Path.GetFileName));
        Assert.DoesNotContain("three", File.ReadAllText(Path.Combine(_directory, "vm-account-passwords.json.protected")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestsWithinWindow_ReuseThePassword()
    {
        var first = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(59));

        var second = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);

        Assert.Equal(first.Password, second.Password);
        Assert.Single(_guest.PasswordsSet);
    }

    [Fact]
    public async Task RequestAfterWindow_RotatesAgain_AndCacheIsCleared()
    {
        var first = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);
        Assert.True(_rotator.HasCachedPassword(VmId, UserId));

        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.False(_rotator.HasCachedPassword(VmId, UserId));

        var second = await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);
        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(2, _guest.PasswordsSet.Count);
    }

    [Fact]
    public async Task ConcurrentRequests_RotateOnce()
    {
        _guest.Delay = TimeSpan.FromMilliseconds(50);

        var results = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None)));

        Assert.Single(_guest.PasswordsSet);
        Assert.Single(results.Select(r => r.Password).Distinct());
    }

    [Fact]
    public async Task DifferentUsers_RotateIndependently()
    {
        await _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None);
        await _rotator.GetPasswordAsync(Target, Guid.NewGuid(), "hh-other", CancellationToken.None);

        Assert.Equal(["hh-owner", "hh-other"], _guest.PasswordsSet.Select(p => p.Account));
    }

    [Fact]
    public async Task FailedRotation_IsNotCached()
    {
        _guest.Failure = new GuestUnavailableException("starting");
        await Assert.ThrowsAsync<GuestUnavailableException>(() => _rotator.GetPasswordAsync(Target, UserId, "hh-owner", CancellationToken.None));

        Assert.False(_rotator.HasCachedPassword(VmId, UserId));
    }

    [Fact]
    public async Task MissingAdminCredential_IsConflict()
    {
        await Assert.ThrowsAsync<GuestAccountConflictException>(() => _rotator.GetPasswordAsync(Target with { VmId = Guid.NewGuid() }, UserId, "hh-owner", CancellationToken.None));
    }

    [Fact]
    public void RotatedPassword_HidesPasswordInToString()
    {
        Assert.DoesNotContain("Secret-123", new RotatedPassword("Secret-123", DateTimeOffset.UtcNow).ToString());
    }
}
