using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.HyperV;

public class VmMapperTests
{
    private static readonly Guid VmA = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid VmB = Guid.Parse("7c1e2d3f-4a5b-4c6d-8e9f-0a1b2c3d4e5f");

    [Theory]
    [InlineData(2, VmState.Running)]
    [InlineData(3, VmState.Off)]
    [InlineData(4, VmState.Stopping)]
    [InlineData(6, VmState.Saved)]
    [InlineData(9, VmState.Paused)]
    [InlineData(10, VmState.Starting)]
    [InlineData(32768, VmState.Paused)]
    [InlineData(32769, VmState.Saved)]
    [InlineData(32770, VmState.Starting)]
    [InlineData(32773, VmState.Saving)]
    [InlineData(32774, VmState.Stopping)]
    [InlineData(32776, VmState.Pausing)]
    [InlineData(32777, VmState.Resuming)]
    [InlineData(0, VmState.Other)]
    [InlineData(5, VmState.Other)]
    public void MapState_TranslatesEnabledState(int enabledState, VmState expected)
    {
        Assert.Equal(expected, VmMapper.MapState((ushort)enabledState));
    }

    [Fact]
    public void Map_RunningVm_IncludesMetricsAddressesAndRdp()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Dev Workstation", 2, 3_600_500)],
            settings: [new SettingsRow(VmA, "Microsoft:Hyper-V:SubType:2")],
            summaries: [new SummaryRow(VmA, 12, 8192)],
            networks: [new GuestNetworkRow(VmA, ["fe80::1", "192.168.1.50", "2001:db8::5"])]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Equal(VmA, vm.Id);
        Assert.Equal("Dev Workstation", vm.Name);
        Assert.Equal(VmState.Running, vm.State);
        Assert.Equal(3600, vm.UptimeSeconds);
        Assert.Equal(12, vm.CpuUsagePercent);
        Assert.Equal(8192, vm.MemoryAssignedMb);
        Assert.Equal(2, vm.Generation);
        Assert.True(vm.RdpAvailable);
        Assert.Equal(["192.168.1.50", "2001:db8::5"], vm.IpAddresses);
    }

    [Fact]
    public void Map_OffVm_OmitsRuntimeValues()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Off VM", 3, 0)],
            settings: [new SettingsRow(VmA, "Microsoft:Hyper-V:SubType:1")],
            summaries: [new SummaryRow(VmA, 0, 0)],
            networks: [new GuestNetworkRow(VmA, ["192.168.1.50"])]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Equal(VmState.Off, vm.State);
        Assert.Null(vm.UptimeSeconds);
        Assert.Null(vm.CpuUsagePercent);
        Assert.Null(vm.MemoryAssignedMb);
        Assert.Equal(1, vm.Generation);
        Assert.False(vm.RdpAvailable);
        Assert.Empty(vm.IpAddresses);
    }

    [Fact]
    public void Map_PausedVm_KeepsMemoryButNotCpuOrRdp()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Paused VM", 9, 60_000)],
            summaries: [new SummaryRow(VmA, 0, 4096)],
            networks: [new GuestNetworkRow(VmA, ["10.0.0.5"])]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Equal(VmState.Paused, vm.State);
        Assert.Equal(60, vm.UptimeSeconds);
        Assert.Null(vm.CpuUsagePercent);
        Assert.Equal(4096, vm.MemoryAssignedMb);
        Assert.False(vm.RdpAvailable);
    }

    [Fact]
    public void Map_RunningVmWithoutGuestAddresses_IsNotRdpAvailable()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "No KVP", 2, 1000)],
            networks: [new GuestNetworkRow(VmA, ["169.254.10.20", "not-an-address", "127.0.0.1"])]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Empty(vm.IpAddresses);
        Assert.False(vm.RdpAvailable);
    }

    [Fact]
    public void Map_MissingSettingsAndSummary_DefaultsToGeneration1AndNullMetrics()
    {
        var snapshot = Snapshot(systems: [new ComputerSystemRow(VmA, "Bare", 2, 0)]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Equal(1, vm.Generation);
        Assert.Null(vm.CpuUsagePercent);
        Assert.Null(vm.MemoryAssignedMb);
    }

    [Fact]
    public void Map_MergesAddressesFromMultipleAdaptersWithoutDuplicates()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Two NICs", 2, 0)],
            networks:
            [
                new GuestNetworkRow(VmA, ["192.168.1.50"]),
                new GuestNetworkRow(VmA, ["10.0.0.7", "192.168.1.50"]),
            ]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Equal(["192.168.1.50", "10.0.0.7"], vm.IpAddresses);
    }

    [Fact]
    public void Map_ClampsProcessorLoadTo100()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Busy", 2, 0)],
            summaries: [new SummaryRow(VmA, 250, null)]);

        Assert.Equal(100, Assert.Single(VmMapper.Map(snapshot)).CpuUsagePercent);
    }

    [Fact]
    public void Map_OrdersByNameIgnoringCase()
    {
        var snapshot = Snapshot(systems:
        [
            new ComputerSystemRow(VmA, "zeta", 3, 0),
            new ComputerSystemRow(VmB, "Alpha", 3, 0),
        ]);

        Assert.Equal(["Alpha", "zeta"], VmMapper.Map(snapshot).Select(vm => vm.Name));
    }

    [Fact]
    public void Map_IgnoresRowsForUnknownVms()
    {
        var snapshot = Snapshot(
            systems: [new ComputerSystemRow(VmA, "Known", 2, 0)],
            summaries: [new SummaryRow(VmB, 50, 1024)],
            networks: [new GuestNetworkRow(VmB, ["192.168.1.99"])]);

        var vm = Assert.Single(VmMapper.Map(snapshot));

        Assert.Null(vm.CpuUsagePercent);
        Assert.Empty(vm.IpAddresses);
    }

    private static HyperVSnapshot Snapshot(
        ComputerSystemRow[]? systems = null,
        SettingsRow[]? settings = null,
        SummaryRow[]? summaries = null,
        GuestNetworkRow[]? networks = null)
    {
        return new HyperVSnapshot(systems ?? [], settings ?? [], summaries ?? [], networks ?? []);
    }
}
