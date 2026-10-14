using System.Net;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Tests.Wake;

public sealed class WakeRelayTests
{
    private static readonly LocalNetwork Lan = new(IPAddress.Parse("192.168.0.10"), IPAddress.Parse("255.255.255.0"));
    private static readonly LocalNetwork Tailnet = new(IPAddress.Parse("100.64.0.1"), IPAddress.Parse("255.192.0.0"));

    private static WakeAdapter Adapter(string ip, string mac = "90-2E-16-66-C5-AE") => new("Ethernet", mac, ip, "255.255.255.255");

    [Fact]
    public void Broadcast_ComesFromTheNetworkMask()
    {
        Assert.Equal(IPAddress.Parse("192.168.0.255"), Lan.Broadcast);
        Assert.Equal(IPAddress.Parse("172.31.255.255"), new LocalNetwork(IPAddress.Parse("172.16.4.2"), IPAddress.Parse("255.240.0.0")).Broadcast);
    }

    [Fact]
    public void Plan_KeepsOnlyAdaptersOnALocalNetwork()
    {
        var plan = WakeRelayPlanner.Plan([Adapter("192.168.0.235"), Adapter("10.0.0.5"), Adapter("192.168.1.5")], [Lan]);

        var packet = Assert.Single(plan);
        Assert.Equal(IPAddress.Parse("192.168.0.10"), packet.Source);
        Assert.Equal([IPAddress.Parse("192.168.0.255"), IPAddress.Broadcast], packet.Targets);
    }

    [Fact]
    public void Plan_NeverTreatsTheTailnetAsALan()
    {
        Assert.Empty(WakeRelayPlanner.Plan([Adapter("100.64.0.9")], [Tailnet]));
    }

    [Theory]
    [InlineData("not a mac")]
    [InlineData("90-2E-16")]
    [InlineData("ZZ-2E-16-66-C5-AE")]
    public void Plan_SkipsAnInvalidMac(string mac)
    {
        Assert.Empty(WakeRelayPlanner.Plan([Adapter("192.168.0.235", mac)], [Lan]));
    }

    [Fact]
    public void MagicPacket_HasTheSyncStreamAndSixteenMacs()
    {
        var mac = new byte[] { 1, 2, 3, 4, 5, 6 };

        var packet = WakeRelayPlanner.MagicPacket(mac);

        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), value => Assert.Equal(0xFF, value));
        for (var repeat = 0; repeat < 16; repeat++)
        {
            Assert.Equal(mac, packet.Skip(6 + (repeat * 6)).Take(6));
        }
    }

    [Fact]
    public void Relay_RejectsAnEmptyOrOversizedRequest()
    {
        var service = new WakeRelayService(new NoNetwork());

        Assert.Throws<InvalidWakeRequestException>(() => service.Relay([]));
        Assert.Throws<InvalidWakeRequestException>(() => service.Relay(Enumerable.Repeat(Adapter("192.168.0.2"), 9).ToList()));
    }

    private sealed class NoNetwork : IWakeRelayNetwork
    {
        public IReadOnlyList<LocalNetwork> Networks() => [];

        public int Send(IPAddress source, IPAddress target, byte[] packet) => 0;
    }
}
