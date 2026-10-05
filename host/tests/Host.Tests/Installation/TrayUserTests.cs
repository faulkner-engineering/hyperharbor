using System.Security.Principal;
using HyperHarbor.Host.Service.Installation;
using Microsoft.Extensions.Configuration;

namespace HyperHarbor.Host.Tests.Installation;

public sealed class TrayUserTests
{
    private const string AccountSid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    [Fact]
    public void Resolve_ReadsConfiguration()
    {
        var sid = TrayUser.Resolve(Configuration(AccountSid), isWindowsService: false);

        Assert.Equal(new SecurityIdentifier(AccountSid), sid);
    }

    [Fact]
    public void Resolve_IsNullForAConsoleRunWithoutConfiguration()
    {
        Assert.Null(TrayUser.Resolve(Configuration(null), isWindowsService: false));
    }

    [Theory]
    [InlineData("S-1-1-0")] // Everyone
    [InlineData("S-1-5-32-545")] // Users
    [InlineData("S-1-5-11")] // Authenticated Users
    [InlineData("S-1-5-4")] // Interactive
    [InlineData("S-1-5-18")] // LocalSystem
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-513")] // Domain Users
    [InlineData("not a sid")]
    public void Resolve_RejectsAnythingButAUserAccount(string value)
    {
        Assert.Throws<InvalidOperationException>(() => TrayUser.Resolve(Configuration(value), isWindowsService: false));
    }

    private static IConfiguration Configuration(string? sid) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [TrayUser.ConfigurationKey] = sid })
            .Build();
}
