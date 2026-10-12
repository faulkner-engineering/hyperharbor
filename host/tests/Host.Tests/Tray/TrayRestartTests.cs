using HyperHarbor.Host.Tray;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Tests.Tray;

public sealed class TrayRestartTests
{
    private static HostUpdateStatus Status(bool supported, string version) => new(
        supported, HostUpdateMode.Auto, "beta", ["beta"], null, version, null, null, HostUpdateActivity.Idle, null, null, null, []);

    [Theory]
    [InlineData(true, "0.1.7-beta.4", true)]
    [InlineData(true, "0.1.7-BETA.3", false)]
    [InlineData(true, "0.1.7-beta.3", false)]
    [InlineData(true, "", false)]
    [InlineData(false, "0.1.7-beta.4", false)]
    public void IsStale_NeedsAnInstalledServiceOnAnotherVersion(bool supported, string serviceVersion, bool expected)
    {
        Assert.Equal(expected, TrayRestart.IsStale(Status(supported, serviceVersion), "0.1.7-beta.3"));
    }

    [Fact]
    public void CurrentExecutable_IsTheJunctionBesideTheVersionsFolder()
    {
        var path = @"C:\Program Files\HyperHarbor\versions\0.1.7-beta.3\HyperHarbor.Host.exe";

        Assert.Equal(@"C:\Program Files\HyperHarbor\current\HyperHarbor.Host.exe", TrayRestart.CurrentExecutable(path));
    }

    [Theory]
    [InlineData(@"C:\Repos\hyperharbor\dist\host\HyperHarbor.Host.exe")]
    [InlineData(@"C:\Program Files\HyperHarbor\current\HyperHarbor.Host.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void CurrentExecutable_IsNullOutsideAVersionFolder(string? path)
    {
        Assert.Null(TrayRestart.CurrentExecutable(path));
    }
}
