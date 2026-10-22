using System.Net;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Host.Core.Wake;

namespace HyperHarbor.Host.Tests.Transfer;

public class TransferAddressesTests
{
    private const ulong Gigabit = 1_000_000_000;

    private static NetworkAdapterState Adapter(
        string name, string address, bool wireless = false, ulong speed = Gigabit, bool gateway = true, bool connected = true, bool physical = true) =>
        new(name, name, "00155D000000", physical, IsWired: !wireless && physical, connected,
            [new Ipv4Assignment(IPAddress.Parse(address), 24)], WakeOnMagicPacket: null,
            HasDefaultGateway: gateway, IsWireless: wireless, LinkSpeed: speed);

    [Fact]
    public void TheLaptop_OffersItsEthernet_BeforeItsFasterLookingWifi()
    {
        // LAPTOP-63M2UH3H on 2026-10-22: a USB 1 Gbps adapter, Wi-Fi 7 reporting 2.4 Gbps, and the Default Switch.
        var ranked = TransferAddresses.Rank(
        [
            Adapter("Wi-Fi", "192.168.0.42", wireless: true, speed: 2_402_000_000),
            Adapter("vEthernet (Default Switch)", "172.17.64.1", speed: 10 * Gigabit, gateway: false, physical: false),
            Adapter("Ethernet 2", "192.168.0.246"),
        ]);

        Assert.Equal(["192.168.0.246", "192.168.0.42"], ranked);
    }

    [Fact]
    public void TheDesktop_OffersItsExternalSwitchAdapter_AndNotTailscaleOrTheDefaultSwitch()
    {
        // TC-PC: the LAN address sits on the external switch's vEthernet adapter, which reports no medium.
        var ranked = TransferAddresses.Rank(
        [
            Adapter("vEthernet (External)", "192.168.0.236", physical: false),
            Adapter("Tailscale", "100.101.102.103", speed: 100 * Gigabit, gateway: false, physical: false),
            Adapter("vEthernet (Default Switch)", "172.20.0.1", speed: 10 * Gigabit, gateway: false, physical: false),
        ]);

        Assert.Equal(["192.168.0.236"], ranked);
    }

    [Fact]
    public void FasterWiredLinksComeFirst_AndDisconnectedOrLinkLocalAddressesAreLeftOut()
    {
        var ranked = TransferAddresses.Rank(
        [
            Adapter("Ethernet", "192.168.1.10"),
            Adapter("Ethernet 2.5G", "192.168.1.11", speed: 2_500_000_000),
            Adapter("Unplugged", "192.168.1.12", speed: 10 * Gigabit, connected: false),
            Adapter("No DHCP", "169.254.10.20"),
        ]);

        Assert.Equal(["192.168.1.11", "192.168.1.10"], ranked);
    }
}
