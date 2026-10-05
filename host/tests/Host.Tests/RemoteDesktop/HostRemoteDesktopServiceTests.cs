using HyperHarbor.Host.Core.RemoteDesktop;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.RemoteDesktop;

public sealed class HostRemoteDesktopServiceTests
{
    [Theory]
    [InlineData("Professional", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Education", true)]
    [InlineData("ServerDatacenter", true)]
    [InlineData("Core", false)]
    [InlineData("CoreN", false)]
    [InlineData("CoreSingleLanguage", false)]
    [InlineData("CoreCountrySpecific", false)]
    public void HomeEditions_AreNotSupported(string editionId, bool supported) =>
        Assert.Equal(supported, HostRemoteDesktopService.IsSupportedEdition(editionId));

    [Fact]
    public void Enable_WhenAlreadyOn_ChangesNothing_EvenUnelevated()
    {
        var settings = new FakeRemoteDesktopSettings { State = new("Professional", "Windows 11 Pro", true, 3389, true) };
        var service = new HostRemoteDesktopService(settings, NullLogger<HostRemoteDesktopService>.Instance, isElevated: false);

        var state = service.Enable();

        Assert.True(state.Enabled);
        Assert.Equal(0, settings.AllowCalls);
    }
}

public sealed class WindowsRemoteDesktopSettingsTests
{
    [LocalHardwareFact]
    public void Read_ReturnsThisHostsSettings()
    {
        var state = new WindowsRemoteDesktopSettings().Read();

        Assert.NotEmpty(state.EditionId);
        Assert.StartsWith("Windows", state.ProductName, StringComparison.Ordinal);
        Assert.InRange(state.Port, 1, 65535);
    }
}
