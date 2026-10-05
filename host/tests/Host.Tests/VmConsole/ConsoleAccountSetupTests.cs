using HyperHarbor.Host.Core.Users;
using HyperHarbor.Host.Core.VmConsole;

namespace HyperHarbor.Host.Tests.VmConsole;

public sealed class ConsoleAccountSetupTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeLocalAccountAdmin _accounts = new();
    private readonly FakeConsoleAccess _access = new();
    private readonly UserStore _users;
    private readonly ConsoleAccountStore _store;
    private readonly ConsoleAccountSetup _setup;

    public ConsoleAccountSetupTests()
    {
        _users = new UserStore(_directory);
        _store = new ConsoleAccountStore(_directory);
        _setup = new ConsoleAccountSetup(_users, _store, _accounts, _access);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void SetUp_CreatesARestrictedAccountPerUserAndStoresItsPassword()
    {
        var results = _setup.SetUp();

        var user = _users.GetOrCreateDefault();
        var result = Assert.Single(results);
        Assert.Equal(new ConsoleAccountResult("owner", "hhc-owner", Created: true), result);
        Assert.Contains("hhc-owner", _accounts.Restricted);
        Assert.Equal(_accounts.Accounts["hhc-owner"], _store.Find(user.UserId)!.Password);
    }

    [Fact]
    public void SetUpAgain_ResetsTheExistingAccount()
    {
        _setup.SetUp();
        var firstPassword = _accounts.Accounts["hhc-owner"];

        var result = Assert.Single(_setup.SetUp());

        Assert.False(result.Created);
        Assert.NotEqual(firstPassword, _accounts.Accounts["hhc-owner"]);
        Assert.Equal(_accounts.Accounts["hhc-owner"], _store.Find(_users.GetOrCreateDefault().UserId)!.Password);
    }

    [Fact]
    public async Task Remove_RevokesAccessDeletesTheAccountAndForgetsIt()
    {
        _setup.SetUp();

        var removed = await _setup.RemoveAsync(CancellationToken.None);

        Assert.Equal(["hhc-owner"], removed);
        Assert.Equal([ConsoleService.Trustee("hhc-owner")], _access.Revokes);
        Assert.Equal(["hhc-owner"], _accounts.Deleted);
        Assert.Empty(_store.List());

        // Its presence means "set up" to the tray and the installer, so it must go with the last account.
        Assert.False(File.Exists(Path.Combine(_directory, ConsoleAccountStore.FileName)));
    }

    [Theory]
    [InlineData("owner", "hhc-owner")]
    [InlineData("Alice Smith", "hhc-alicesmith")]
    [InlineData("averyveryverylongusername", "hhc-averyveryverylon")]
    public void AccountNames_FitWindowsLimits(string userName, string expected)
    {
        Assert.Equal(expected, ConsoleAccountName.For(userName));
    }
}
