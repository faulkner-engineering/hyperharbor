using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;

namespace HyperHarbor.Host.Tests.GameReady;

/// <summary>The LSA secret write path, with the secret store and the Winlogon key replaced.</summary>
public sealed class WindowsConsoleSignInSystemTests
{
    private sealed class FakeLsa : ILsaSecrets
    {
        public Dictionary<string, string> Secrets { get; } = [];

        public void Store(string name, string? value)
        {
            if (value is null)
            {
                Secrets.Remove(name);
            }
            else
            {
                Secrets[name] = value;
            }
        }

        public bool Exists(string name) => Secrets.ContainsKey(name);
    }

    private sealed class FakeWinlogon : IWinlogonValues
    {
        public Dictionary<string, string> Values { get; } = [];

        public List<string> Writes { get; } = [];

        public string? Get(string name) => Values.GetValueOrDefault(name);

        public void Set(string name, string value)
        {
            Writes.Add(name);
            Values[name] = value;
        }

        public void Remove(string name) => Values.Remove(name);
    }

    private readonly FakeLsa _lsa = new();
    private readonly FakeWinlogon _winlogon = new();

    private WindowsConsoleSignInSystem Create() => new(_winlogon, _lsa);

    [Fact]
    public void ThePassword_GoesToTheDefaultPasswordSecret_NeverToTheRegistry()
    {
        var system = Create();

        system.StoreAutoSignInPassword("s3cret-Value");
        system.WriteWinlogon(new WinlogonSettings("1", "Console", "PC"));

        Assert.Equal("s3cret-Value", _lsa.Secrets["DefaultPassword"]);
        Assert.True(system.HasAutoSignInPassword());
        Assert.DoesNotContain("DefaultPassword", _winlogon.Writes);
        Assert.DoesNotContain("s3cret-Value", _winlogon.Values.Values);
        Assert.Equal(["AutoAdminLogon", "DefaultUserName", "DefaultDomainName"], _winlogon.Writes);
    }

    [Fact]
    public void ANullPassword_DeletesTheSecret()
    {
        var system = Create();
        system.StoreAutoSignInPassword("x");

        system.StoreAutoSignInPassword(null);

        Assert.False(system.HasAutoSignInPassword());
    }

    [Fact]
    public void WriteWinlogon_RemovesAbsentValues_AndTheSignInCount()
    {
        _winlogon.Values["AutoLogonCount"] = "3";
        _winlogon.Values["DefaultDomainName"] = "OLD";
        var system = Create();

        system.WriteWinlogon(new WinlogonSettings(null, "tyler", null));

        Assert.False(_winlogon.Values.ContainsKey("AutoAdminLogon"));
        Assert.False(_winlogon.Values.ContainsKey("DefaultDomainName"));
        Assert.False(_winlogon.Values.ContainsKey("AutoLogonCount"));
        Assert.Equal("tyler", _winlogon.Values["DefaultUserName"]);
    }

    [Fact]
    public void APlaintextPassword_IsDetected_AndRemoved()
    {
        _winlogon.Values["DefaultPassword"] = "old";
        var system = Create();
        Assert.True(system.HasPlaintextPassword());

        system.RemovePlaintextPassword();

        Assert.False(system.HasPlaintextPassword());
    }
}
