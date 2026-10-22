using System.Net;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Tests.Wake;

public class WakeReadinessTests
{
    [Fact]
    public void ReadyDesktop_PassesEveryCheck()
    {
        var readiness = WakeReadinessEvaluator.Evaluate(WakeScenarios.ReadyDesktop());

        Assert.True(readiness.Ready);
        Assert.All(readiness.Checks, check => Assert.Equal(WakeCheckStatus.Pass, check.Status));
        Assert.Equal(
            [WakeCheckIds.WiredAdapter, WakeCheckIds.NicWakeOnMagicPacket, WakeCheckIds.NicAllowWake, WakeCheckIds.SleepKeepsNetwork, WakeCheckIds.FastStartupDisabled, WakeCheckIds.PrivateNetwork],
            readiness.Checks.Select(check => check.Id));
    }

    [Fact]
    public void PublicLanNetwork_Warns_WithTheCommandForThatConnection_AndNoAutomaticFix()
    {
        // TC-PC on 2026-10-21: its LAN address was on the external switch's vEthernet adapter, which Windows made Public.
        var environment = WakeScenarios.ReadyDesktop();
        var external = new NetworkAdapterState("vEthernet (External)", "Hyper-V Virtual Ethernet Adapter", "902E1666C5AF", IsPhysical: false,
            IsWired: true, IsConnected: true, [new Ipv4Assignment(IPAddress.Parse("192.168.0.236"), 24)], null, NetworkCategory.Public, HasDefaultGateway: true);
        var readiness = WakeReadinessEvaluator.Evaluate(environment with { Adapters = [.. environment.Adapters, external] });

        var check = Assert.Single(readiness.Checks, check => check.Id == WakeCheckIds.PrivateNetwork);
        Assert.Equal(WakeCheckStatus.Warn, check.Status);
        Assert.False(check.AutoFixAvailable);
        Assert.StartsWith("vEthernet (External) is on a Public network", check.Detail, StringComparison.Ordinal);
        Assert.Contains("Set-NetConnectionProfile -InterfaceAlias 'vEthernet (External)' -NetworkCategory Private", check.Detail, StringComparison.Ordinal);
        Assert.True(readiness.Ready);
    }

    [Fact]
    public void PublicNetworkWithoutAGateway_LikeTheDefaultSwitch_DoesNotCount()
    {
        var environment = WakeScenarios.ReadyDesktop();
        var defaultSwitch = new NetworkAdapterState("vEthernet (Default Switch)", "Hyper-V Virtual Ethernet Adapter", "00155D000001", IsPhysical: false,
            IsWired: true, IsConnected: true, [new Ipv4Assignment(IPAddress.Parse("172.20.240.1"), 20)], null, NetworkCategory.Public, HasDefaultGateway: false);
        var readiness = WakeReadinessEvaluator.Evaluate(environment with { Adapters = [.. environment.Adapters, defaultSwitch] });

        var check = Assert.Single(readiness.Checks, check => check.Id == WakeCheckIds.PrivateNetwork);
        Assert.Equal(WakeCheckStatus.Pass, check.Status);
        Assert.Equal("Ethernet is on a private network.", check.Detail);
    }

    [Fact]
    public void DomainNetwork_Warns_ThatTheFirewallRuleNeedsTheDomainProfile()
    {
        var environment = WakeScenarios.ReadyDesktop();
        var readiness = WakeReadinessEvaluator.Evaluate(environment with
        {
            Adapters = [environment.Adapters[0] with { Category = NetworkCategory.DomainAuthenticated }],
        });

        var check = Assert.Single(readiness.Checks, check => check.Id == WakeCheckIds.PrivateNetwork);
        Assert.Equal(WakeCheckStatus.Warn, check.Status);
        Assert.Contains("add the Domain profile", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void WifiLaptop_FailsWithFixableChecksMarked()
    {
        var readiness = WakeReadinessEvaluator.Evaluate(WakeScenarios.WifiLaptop());
        var checks = readiness.Checks.ToDictionary(check => check.Id);

        Assert.False(readiness.Ready);
        Assert.Equal(WakeCheckStatus.Fail, checks[WakeCheckIds.WiredAdapter].Status);
        Assert.False(checks[WakeCheckIds.WiredAdapter].AutoFixAvailable);
        Assert.Contains("Ethernet is not connected", checks[WakeCheckIds.WiredAdapter].Detail);

        Assert.Equal(WakeCheckStatus.Fail, checks[WakeCheckIds.NicWakeOnMagicPacket].Status);
        Assert.True(checks[WakeCheckIds.NicWakeOnMagicPacket].AutoFixAvailable);

        // Not wake-programmable, so powercfg cannot change it; offering a fix would fail.
        Assert.Equal(WakeCheckStatus.Fail, checks[WakeCheckIds.NicAllowWake].Status);
        Assert.False(checks[WakeCheckIds.NicAllowWake].AutoFixAvailable);

        // Forced off by Group Policy.
        Assert.Equal(WakeCheckStatus.Fail, checks[WakeCheckIds.SleepKeepsNetwork].Status);
        Assert.False(checks[WakeCheckIds.SleepKeepsNetwork].AutoFixAvailable);
        Assert.Contains("Group Policy", checks[WakeCheckIds.SleepKeepsNetwork].Detail);

        Assert.Equal(WakeCheckStatus.Pass, checks[WakeCheckIds.FastStartupDisabled].Status);
    }

    [Fact]
    public void AdapterProgrammableButNotArmed_IsFixable()
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            WakeArmedDevices = new HashSet<string>(),
        };

        var check = Check(environment, WakeCheckIds.NicAllowWake);

        Assert.Equal(WakeCheckStatus.Fail, check.Status);
        Assert.True(check.AutoFixAvailable);
    }

    [Theory]
    [InlineData(1u, WakeCheckStatus.Pass)]
    [InlineData(0u, WakeCheckStatus.Fail)]
    public void GroupPolicy_OverridesSchemeValue(uint policy, WakeCheckStatus expected)
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            Power = new PowerState(false, ModernStandby: true, StandbyConnectivitySupported: true, StandbyConnectivityAc: 1 - policy, false, policy),
        };

        var check = Check(environment, WakeCheckIds.SleepKeepsNetwork);

        Assert.Equal(expected, check.Status);
        Assert.False(check.AutoFixAvailable);
    }

    [Fact]
    public void Fixer_RefusesChecksThatCannotSucceed()
    {
        // Runs without elevation: both checks are rejected before any command executes.
        var results = WakeFixer.Apply(WakeScenarios.WifiLaptop(), [WakeCheckIds.NicAllowWake, WakeCheckIds.SleepKeepsNetwork]);

        Assert.All(results, result => Assert.False(result.Applied));
        Assert.Contains("Group Policy", results.Single(r => r.CheckId == WakeCheckIds.SleepKeepsNetwork).Detail);
    }

    [Fact]
    public void UnknownMagicPacketSetting_IsWarning()
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            Adapters = [WakeScenarios.Ethernet(connected: true, address: "192.168.1.20", wakeOnMagicPacket: null)],
        };

        var check = Check(environment, WakeCheckIds.NicWakeOnMagicPacket);

        Assert.Equal(WakeCheckStatus.Warn, check.Status);
        Assert.False(check.AutoFixAvailable);
    }

    [Theory]
    [InlineData(1u, WakeCheckStatus.Pass)]
    [InlineData(2u, WakeCheckStatus.Pass)]
    [InlineData(0u, WakeCheckStatus.Fail)]
    public void ModernStandby_DependsOnConnectivityPolicy(uint policy, WakeCheckStatus expected)
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            Power = new PowerState(false, ModernStandby: true, StandbyConnectivitySupported: true, policy, false),
        };

        Assert.Equal(expected, Check(environment, WakeCheckIds.SleepKeepsNetwork).Status);
    }

    [Fact]
    public void ModernStandbyWithoutConnectivitySupport_FailsWithoutFix()
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            Power = new PowerState(false, ModernStandby: true, StandbyConnectivitySupported: false, null, false),
        };

        var check = Check(environment, WakeCheckIds.SleepKeepsNetwork);

        Assert.Equal(WakeCheckStatus.Fail, check.Status);
        Assert.False(check.AutoFixAvailable);
    }

    [Fact]
    public void FastStartupOn_IsFixableWarning_AndDoesNotBlockReady()
    {
        var environment = WakeScenarios.ReadyDesktop() with
        {
            Power = WakeScenarios.ReadyDesktop().Power with { FastStartupEnabled = true },
        };

        var readiness = WakeReadinessEvaluator.Evaluate(environment);
        var check = readiness.Checks.Single(c => c.Id == WakeCheckIds.FastStartupDisabled);

        Assert.Equal(WakeCheckStatus.Warn, check.Status);
        Assert.True(check.AutoFixAvailable);
        Assert.True(readiness.Ready);
    }

    [Fact]
    public void WakeInfo_UsesWiredAdapterWithBroadcastAddress()
    {
        var info = WakeInfoBuilder.Build(WakeScenarios.ReadyDesktop());

        var adapter = Assert.Single(info.Adapters);
        Assert.Equal("Ethernet", adapter.Name);
        Assert.Equal("90-2E-16-66-C5-AE", adapter.MacAddress);
        Assert.Equal("192.168.1.20", adapter.Ipv4Address);
        Assert.Equal("192.168.1.255", adapter.BroadcastAddress);
    }

    [Fact]
    public void WakeInfo_ExcludesWifiAndLinkLocal()
    {
        Assert.Empty(WakeInfoBuilder.Build(WakeScenarios.WifiLaptop()).Adapters);
    }

    [Fact]
    public void WakeInfo_TakesAddressFromHyperVExternalSwitch()
    {
        // With an external switch the physical adapter has no IP; the vEthernet adapter with the same MAC does.
        var physical = WakeScenarios.Ethernet(connected: true, address: "169.254.1.1", wakeOnMagicPacket: true, prefix: 16);
        var vEthernet = new NetworkAdapterState(
            "vEthernet (External)",
            "Hyper-V Virtual Ethernet Adapter",
            physical.MacAddress,
            IsPhysical: false,
            IsWired: true,
            IsConnected: true,
            [new Ipv4Assignment(IPAddress.Parse("10.0.0.40"), 23)],
            null);
        var environment = WakeScenarios.ReadyDesktop() with { Adapters = [physical, vEthernet] };

        var adapter = Assert.Single(WakeInfoBuilder.Build(environment).Adapters);

        Assert.Equal("Ethernet", adapter.Name);
        Assert.Equal("90-2E-16-66-C5-AE", adapter.MacAddress);
        Assert.Equal("10.0.0.40", adapter.Ipv4Address);
        Assert.Equal("10.0.1.255", adapter.BroadcastAddress);
    }

    [Theory]
    [InlineData("192.168.0.70", 24, "192.168.0.255")]
    [InlineData("10.2.3.4", 8, "10.255.255.255")]
    [InlineData("172.25.176.1", 20, "172.25.191.255")]
    [InlineData("192.168.1.9", 32, "192.168.1.9")]
    public void Broadcast_IsComputedFromPrefix(string address, int prefix, string expected)
    {
        Assert.Equal(expected, new Ipv4Assignment(IPAddress.Parse(address), prefix).Broadcast.ToString());
    }

    private static WakeCheck Check(WakeEnvironment environment, string id) =>
        WakeReadinessEvaluator.Evaluate(environment).Checks.Single(check => check.Id == id);
}
