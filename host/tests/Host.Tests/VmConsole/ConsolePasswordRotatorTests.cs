using HyperHarbor.Host.Core.VmConsole;
using HyperHarbor.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.VmConsole;

public sealed class ConsolePasswordRotatorTests : IDisposable
{
    private const string Account = "hhc-owner";
    private const string InitialPassword = "Console-Initial1!";
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeConsolePasswordChanger _changer = new();
    private readonly ConsoleAccountStore _store;
    private readonly ConsolePasswordRotator _rotator;

    public ConsolePasswordRotatorTests()
    {
        _store = new ConsoleAccountStore(_directory);
        _store.Save(UserId, new ConsoleCredential(Account, InitialPassword));
        _changer.Passwords[Account] = InitialPassword;
        _rotator = new ConsolePasswordRotator(_store, _changer, _time, TimeSpan.FromSeconds(60), NullLogger<ConsolePasswordRotator>.Instance);
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
    public async Task FirstRequest_ChangesThePasswordAndStoresIt()
    {
        var result = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);

        Assert.Equal(result.Password, _changer.Passwords[Account]);
        Assert.NotEqual(InitialPassword, result.Password);
        Assert.Equal(result.Password, _store.Find(UserId)!.Password);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(60), result.ExpiresAt);
    }

    [Fact]
    public async Task RequestsWithinTheWindow_GetTheSamePassword()
    {
        var first = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(30));
        var second = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);

        Assert.Equal(first.Password, second.Password);
        Assert.Single(_changer.Changes);
    }

    [Fact]
    public async Task ConcurrentRequests_RotateOnce()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _rotator.GetPasswordAsync(UserId, CancellationToken.None)));

        Assert.Single(results.Select(result => result.Password).Distinct());
        Assert.Single(_changer.Changes);
    }

    [Fact]
    public async Task WhenTheWindowEnds_TheDeliveredPasswordIsRotatedAway()
    {
        var delivered = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);

        _time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => _changer.Changes.Count == 2);

        Assert.False(_rotator.HasCachedPassword(UserId));
        Assert.NotEqual(delivered.Password, _changer.Passwords[Account]);
        Assert.Equal(_changer.Passwords[Account], _store.Find(UserId)!.Password);
    }

    [Fact]
    public async Task RequestAfterTheWindow_GetsANewPassword()
    {
        var first = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntilAsync(() => _changer.Changes.Count == 2);

        var second = await _rotator.GetPasswordAsync(UserId, CancellationToken.None);

        Assert.NotEqual(first.Password, second.Password);
        Assert.Equal(second.Password, _changer.Passwords[Account]);
    }

    [Fact]
    public async Task NoStoredAccount_RequiresSetup()
    {
        var error = await Assert.ThrowsAsync<ConsoleConflictException>(() => _rotator.GetPasswordAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.ConsoleSetupRequired, error.Code);
    }

    [Fact]
    public async Task OutOfDateStoredPassword_RequiresSetupAndKeepsTheStore()
    {
        _changer.Passwords[Account] = "Changed-Elsewhere1!";

        var error = await Assert.ThrowsAsync<ConsoleConflictException>(() => _rotator.GetPasswordAsync(UserId, CancellationToken.None));

        Assert.Equal(ContractInfo.ProblemCodes.ConsoleSetupRequired, error.Code);
        Assert.Equal(InitialPassword, _store.Find(UserId)!.Password);
        Assert.False(_rotator.HasCachedPassword(UserId));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
