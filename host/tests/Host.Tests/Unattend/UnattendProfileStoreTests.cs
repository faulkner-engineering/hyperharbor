using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

public sealed class UnattendProfileStoreTests : IDisposable
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIDk4Zk2uK7xVQbV3mN6PZ8o2jQfV0uKZp1Gk3m8rT0aB user@laptop";
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly UnattendProfileStore _store;

    public UnattendProfileStoreTests()
    {
        _store = new UnattendProfileStore(_directory, () => "Central Standard Time");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static UnattendProfileRequest Windows(string name = "Gaming") =>
        new(name, InstallOs.Windows, Windows: new WindowsInstallSettings("Windows 11 Pro"));

    private static UnattendProfileRequest Linux(string name = "Server") =>
        new(name, InstallOs.Linux, Linux: new LinuxInstallSettings([Key], ["git"]));

    [Fact]
    public void BuiltIns_AreTheThreeStartingProfilesInTheHostsTimeZone()
    {
        var builtIns = _store.List(Owner);

        Assert.Equal(["windows-workstation", "windows-burner", "ubuntu-dev-server"], builtIns.Select(profile => profile.Id));
        Assert.All(builtIns, profile => Assert.True(profile.BuiltIn));
        Assert.True(builtIns[1].Windows!.BypassHardwareChecks);
        Assert.False(builtIns[0].Windows!.BypassHardwareChecks);
        Assert.Equal("Central Standard Time", builtIns[0].TimeZone);
        Assert.Equal("America/Chicago", builtIns[2].TimeZone);
    }

    [Fact]
    public void Created_ProfilesBelongToTheirUserAndSurviveARestart()
    {
        var created = _store.Create(Owner, Linux());

        var reloaded = new UnattendProfileStore(_directory, () => "Central Standard Time");
        Assert.Equivalent(created, reloaded.Get(Owner, created.Id), strict: true);
        Assert.Equal("America/Chicago", created.TimeZone);
        Assert.False(created.BuiltIn);
        Assert.Throws<UnattendProfileNotFoundException>(() => reloaded.Get(Other, created.Id));
        Assert.Equal(3, reloaded.List(Other).Count);
    }

    [Fact]
    public void UpdateAndDelete_ChangeOnlyTheUsersOwnProfiles()
    {
        var created = _store.Create(Owner, Windows());

        var updated = _store.Update(Owner, created.Id, Windows("Renamed"));
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(created.Id, updated.Id);
        Assert.Throws<UnattendProfileNotFoundException>(() => _store.Update(Other, created.Id, Windows()));

        _store.Delete(Owner, created.Id);
        Assert.Equal(3, _store.List(Owner).Count);
    }

    [Fact]
    public void BuiltIns_CannotBeChanged()
    {
        var error = Assert.Throws<LifecycleConflictException>(() => _store.Update(Owner, "windows-burner", Windows()));

        Assert.Equal(UnattendProfileStore.ProfileReadOnlyCode, error.Code);
        Assert.Throws<LifecycleConflictException>(() => _store.Delete(Owner, "ubuntu-dev-server"));
    }

    [Theory]
    [InlineData("adminAccountName", "Administrator")]
    [InlineData("adminAccountName", "hh-owner")]
    [InlineData("adminAccountName", "bad name")]
    [InlineData("timeZone", "Mars/Olympus")]
    [InlineData("locale", "english")]
    [InlineData("name", "")]
    public void InvalidWindowsFields_AreReported(string field, string value)
    {
        var request = Windows() with
        {
            Name = field == "name" ? value : "Valid",
            AdminAccountName = field == "adminAccountName" ? value : "hhadmin",
            TimeZone = field == "timeZone" ? value : null,
            Locale = field == "locale" ? value : "en-US",
        };

        var error = Assert.Throws<LifecycleValidationException>(() => _store.Create(Owner, request));

        Assert.Contains(error.Errors, issue => issue.Field == field);
    }

    [Theory]
    [InlineData("linux.sshAuthorizedKeys", "not a key", "git")]
    [InlineData("linux.sshAuthorizedKeys", "ssh-ed25519 AAAA\nruncmd", "git")]
    [InlineData("linux.packages", Key, "git; rm -rf /")]
    [InlineData("linux.packages", Key, "Git")]
    public void InvalidLinuxSettings_AreReported(string field, string key, string package)
    {
        var request = Linux() with { Linux = new LinuxInstallSettings([key], [package]) };

        var error = Assert.Throws<LifecycleValidationException>(() => _store.Create(Owner, request));

        Assert.Contains(error.Errors, issue => issue.Field == field);
    }

    [Fact]
    public void LinuxAccountNames_MustBeLowercase()
    {
        var error = Assert.Throws<LifecycleValidationException>(() => _store.Create(Owner, Linux() with { AdminAccountName = "HHAdmin" }));

        Assert.Contains(error.Errors, issue => issue.Field == "adminAccountName");
    }

    [Fact]
    public void SettingsMustMatchTheOs()
    {
        var missing = Assert.Throws<LifecycleValidationException>(() => _store.Create(Owner, new UnattendProfileRequest("W", InstallOs.Windows)));
        Assert.Contains(missing.Errors, issue => issue.Field == "windows");

        var mixed = Assert.Throws<LifecycleValidationException>(() => _store.Create(Owner, Linux() with { Windows = new WindowsInstallSettings("Windows 11 Pro") }));
        Assert.Contains(mixed.Errors, issue => issue.Field == "windows");
    }
}
