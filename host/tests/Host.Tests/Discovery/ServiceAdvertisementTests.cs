using HyperHarbor.Host.Core.Discovery;

namespace HyperHarbor.Host.Tests.Discovery;

public class ServiceAdvertisementTests
{
    private static readonly Guid HostId = Guid.Parse("6f1c2b8e-3a4d-4c55-9e1f-2b7a9d0c4e11");

    [Fact]
    public void Create_BuildsNamesAndTxtProperties()
    {
        var ad = ServiceAdvertisement.Create("GAMING-PC", 48443, HostId, "1.0.0", "0.1.0");

        Assert.Equal("GAMING-PC._hyperharbor._tcp.local", ad.FullInstanceName);
        Assert.Equal("GAMING-PC.local", ad.FullHostName);
        Assert.Equal(48443, ad.Port);
        Assert.Equal("6f1c2b8e-3a4d-4c55-9e1f-2b7a9d0c4e11", ad.Properties["id"]);
        Assert.Equal("1.0.0", ad.Properties["api"]);
        Assert.Equal("0.1.0", ad.Properties["ver"]);
    }

    [Fact]
    public void Create_SanitizesAndTruncatesInstanceLabel()
    {
        var ad = ServiceAdvertisement.Create("pc.example" + new string('x', 80), 48443, HostId, "1.0.0", "0.1.0");

        Assert.DoesNotContain('.', ad.InstanceName);
        Assert.Equal(63, ad.InstanceName.Length);
    }
}
