using System.Security.Principal;
using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.GameReady;

public sealed class ConsoleSignInTests
{
    private readonly FakeConsoleSignInSystem _system = new();
    private readonly MemoryConsoleSignInStore _store = new();

    private ConsoleSignInService Create() => new(_system, _store, NullLogger<ConsoleSignInService>.Instance);

    [Fact]
    public void Enable_StoresThePasswordInTheSecretAndTheVault_AndTurnsAutoSignInOn()
    {
        Create().Enable("Console", ConsoleLaunchers.SteamBigPicture);

        Assert.NotNull(_system.Secret);
        Assert.Equal(_system.Secret, _store.Record!.Password);
        Assert.True(_system.Secret!.Length >= 32);
        Assert.Equal(new WinlogonSettings("1", "Console", Environment.MachineName), _system.Winlogon);
        Assert.False(_system.Plaintext);
        Assert.Equal(new ConsoleLockSetting(0, 0), _system.Lock);
        Assert.True(_store.Record.CreatedUser);
        Assert.DoesNotContain(_store.Record.Password, _store.Record.ToString());
    }

    [Fact]
    public void Enable_RefusesAnAdministrator_AndUndoesTheAccount()
    {
        _system.Groups = [FakeConsoleSignInSystem.UsersGroup, FakeConsoleSignInSystem.Administrators];

        var error = Assert.Throws<ConsoleSignInException>(() => Create().Enable("Console", ConsoleLaunchers.SteamBigPicture));

        Assert.Contains("standard user", error.Message);
        Assert.Empty(_system.Accounts);
        Assert.Null(_store.Record);
        Assert.Null(_system.Secret);
        Assert.DoesNotContain("storeSecret", _system.Calls);
    }

    [Fact]
    public void Enable_RefusesHyperVAdministrators()
    {
        _system.Groups = [FakeConsoleSignInSystem.HyperVAdministrators];

        Assert.Throws<ConsoleSignInException>(() => Create().Enable("Console", ConsoleLaunchers.SteamBigPicture));
        Assert.Empty(_system.Accounts);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void IsPrivileged_FindsTheGroupsThatAdministerTheHost(bool admins, bool hyperV)
    {
        List<SecurityIdentifier> groups = [FakeConsoleSignInSystem.UsersGroup];
        if (admins)
        {
            groups.Add(FakeConsoleSignInSystem.Administrators);
        }

        if (hyperV)
        {
            groups.Add(FakeConsoleSignInSystem.HyperVAdministrators);
        }

        Assert.Equal(admins || hyperV, ConsoleUserPolicy.IsPrivileged(groups));
    }

    [Theory]
    [InlineData("Console", null)]
    [InlineData("", "Enter")]
    [InlineData("Administrator", "Windows account")]
    [InlineData("hh-owner", "hh-")]
    [InlineData("a/b", "cannot contain")]
    [InlineData("a-name-that-is-way-too-long", "20")]
    public void ValidateName_AcceptsOnlyUsableNames(string name, string? expected)
    {
        var result = ConsoleUserPolicy.ValidateName(name);
        if (expected is null)
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Contains(expected, result);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enable_RefusesAnAccountItDidNotCreate_AndExistingAutoSignIn(bool autoSignIn)
    {
        if (autoSignIn)
        {
            _system.Winlogon = new WinlogonSettings("1", "tyler", "PC");
        }
        else
        {
            _system.Accounts.Add("Console");
        }

        Assert.Throws<ConsoleSignInException>(() => Create().Enable("Console", ConsoleLaunchers.SteamBigPicture));

        Assert.Empty(_system.Calls);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void Enable_UndoesEveryFinishedStep_InReverse_WhenALaterStepFails()
    {
        _system.Winlogon = new WinlogonSettings(null, "tyler", "PC");
        _system.Lock = new ConsoleLockSetting(1, 5);
        _system.FailOn.Add("writeWinlogon");

        var error = Assert.Throws<ConsoleSignInException>(() => Create().Enable("Console", ConsoleLaunchers.SteamBigPicture));

        Assert.Contains("writeWinlogon failed", error.Message);
        Assert.Null(_store.Record);
        Assert.Null(_system.Secret);
        Assert.Empty(_system.Accounts);
        Assert.Empty(_system.RunValues);
        Assert.Equal(new ConsoleLockSetting(1, 5), _system.Lock);

        // The failed write is followed by the undo of the finished steps, newest first. The Winlogon values were never
        // changed, so they are restored only by the step that failed first, not by an undo.
        var afterFailure = _system.Calls.SkipWhile(call => call != "writeWinlogon").Skip(1).ToList();
        Assert.Equal(["deleteSecret", "writeLock", "removeRunValues", "deleteUser"], afterFailure);
    }

    [Fact]
    public void Enable_ReportsWhatCouldNotBeUndone()
    {
        _system.FailOn.Add("configureHive");
        _system.FailOn.Add("deleteUser");

        var error = Assert.Throws<ConsoleSignInException>(() => Create().Enable("Console", ConsoleLaunchers.SteamBigPicture));

        Assert.Contains("could not be undone", error.Message);
        Assert.Contains("delete the account", error.Message);
    }

    [Fact]
    public void Disable_RestoresWhatSetupChanged_AndKeepsTheUserByDefault()
    {
        _system.Winlogon = new WinlogonSettings(null, "tyler", "PC");
        _system.Lock = new ConsoleLockSetting(1, 5);
        var service = Create();
        service.Enable("Console", ConsoleLaunchers.SteamBigPicture);

        service.Disable(deleteUser: false);

        Assert.Equal(new WinlogonSettings(null, "tyler", "PC"), _system.Winlogon);
        Assert.Equal(new ConsoleLockSetting(1, 5), _system.Lock);
        Assert.Null(_system.Secret);
        Assert.Null(_store.Record);
        Assert.Contains("Console", _system.Accounts);
        Assert.Empty(_system.RunValues);
    }

    [Fact]
    public void Disable_CanDeleteTheUserItCreated()
    {
        var service = Create();
        service.Enable("Console", ConsoleLaunchers.SteamBigPicture);

        service.Disable(deleteUser: true);

        Assert.Empty(_system.Accounts);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void Disable_ContinuesPastAFailure_AndKeepsTheRecordForAnotherTry()
    {
        var service = Create();
        service.Enable("Console", ConsoleLaunchers.SteamBigPicture);
        _system.FailOn.Add("deleteUser");

        var error = Assert.Throws<ConsoleSignInException>(() => service.Disable(deleteUser: true));

        Assert.Contains("delete the account", error.Message);
        Assert.False(_system.Winlogon.AutoSignInOn);
        Assert.Null(_system.Secret);
        Assert.NotNull(_store.Record);

        _system.FailOn.Clear();
        service.Disable(deleteUser: true);
        Assert.Empty(_system.Accounts);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void Disable_WithoutASetup_Refuses()
    {
        Assert.Throws<ConsoleSignInException>(() => Create().Disable(deleteUser: true));
        Assert.Empty(_system.Calls);
    }

    [Fact]
    public void Read_ReportsTheFourItems_AndTheCardTurnsReady()
    {
        var service = Create();
        Assert.False(service.Read().Configured);

        service.Enable("Console", ConsoleLaunchers.SteamBigPicture);
        var reading = service.Read();
        var card = GameReadyCards.ConsoleSignIn(reading, elevated: true);

        Assert.True(reading.Healthy);
        Assert.Equal(GameReadyStates.Ready, card.State);
        Assert.Equal(4, card.Checks.Count);
        Assert.All(card.Checks, check => Assert.Equal(GameReadyCheckStatuses.Pass, check.Status));
        Assert.Contains(card.Hints, hint => hint.Contains("physical access"));
    }

    [Fact]
    public void Read_NoticesDrift_PlaintextPasswords_AndTheHelloWarning()
    {
        var service = Create();
        service.Enable("Console", ConsoleLaunchers.SteamBigPicture);
        _system.RunValues.Clear();
        _system.Plaintext = true;
        _system.Admins = new AdminAccountReport(["tyler@example.com"], WindowsHelloRequired: true);

        var card = GameReadyCards.ConsoleSignIn(service.Read(), elevated: true);

        Assert.Equal(GameReadyStates.Attention, card.State);
        Assert.Contains(card.Checks, check => check.Id == "launcher" && check.Status == GameReadyCheckStatuses.Fail);
        Assert.Contains(card.Hints, hint => hint.Contains("plain text"));
        Assert.Contains(card.Hints, hint => hint.Contains("Microsoft account") && hint.Contains("not for the local Console user"));
    }

    [Fact]
    public void TheHelloWarning_NeedsBothAMicrosoftAdministratorAndHelloRequired()
    {
        Assert.False(new AdminAccountReport(["a@b.c"], false).BlocksAdminAutoSignIn);
        Assert.False(new AdminAccountReport([], true).BlocksAdminAutoSignIn);
        Assert.True(new AdminAccountReport(["a@b.c"], true).BlocksAdminAutoSignIn);
    }

    [Fact]
    public void GeneratedPasswords_AreLong_Mixed_AndDifferent()
    {
        var first = ConsoleSignInService.GeneratePassword();
        var second = ConsoleSignInService.GeneratePassword();

        Assert.NotEqual(first, second);
        Assert.Equal(32, first.Length);
        Assert.Contains(first, char.IsUpper);
        Assert.Contains(first, char.IsLower);
        Assert.Contains(first, char.IsDigit);
        Assert.Contains(first, c => !char.IsLetterOrDigit(c));
    }
}
