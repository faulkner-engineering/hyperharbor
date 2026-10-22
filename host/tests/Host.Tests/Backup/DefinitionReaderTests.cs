using HyperHarbor.Host.Core.Backup;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class DefinitionReaderTests
{
    [Theory]
    // As Hyper-V writes a connection's HostResource (seen live): CreationClassName comes first.
    [InlineData(@"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch"",Name=""C08CB7B8-9B3C-408E-8E30-5E16A3AEB444""",
        "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444")]
    [InlineData(@"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.Name=""1d6e5b3c-0000-4000-8000-000000000001"",CreationClassName=""Msvm_VirtualEthernetSwitch""",
        "1d6e5b3c-0000-4000-8000-000000000001")]
    [InlineData(@"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch""", null)]
    public void SwitchIdOf_ReadsTheNameKey_NotCreationClassName(string path, string? expected)
    {
        Assert.Equal(expected, CimHyperVDefinitionReader.SwitchIdOf(path));
    }

    [Fact]
    public void ParentMatches_ComparesTheInstanceId_WithDoubledBackslashes()
    {
        const string parent = @"\\HOST\root\virtualization\v2:Msvm_SyntheticEthernetPortSettingData.InstanceID=""Microsoft:0B9A\\C1D2""";

        Assert.True(CimHyperVDefinitionReader.ParentMatches(parent, @"Microsoft:0B9A\C1D2"));
        Assert.False(CimHyperVDefinitionReader.ParentMatches(parent, @"Microsoft:0B9A\C1D3"));
    }

    private const string SwitchA = "1d6e5b3c-0000-4000-8000-00000000000a";
    private const string SwitchB = "1d6e5b3c-0000-4000-8000-00000000000b";

    private static string PortPath(string instanceId) =>
        $@"\\HOST\root\virtualization\v2:Msvm_SyntheticEthernetPortSettingData.InstanceID=""{instanceId.Replace(@"\", @"\\")}""";

    private static string SwitchPath(string id) =>
        $@"\\HOST\root\virtualization\v2:Msvm_VirtualEthernetSwitch.CreationClassName=""Msvm_VirtualEthernetSwitch"",Name=""{id}""";

    private static readonly Dictionary<string, string> Switches = new(StringComparer.OrdinalIgnoreCase) { [SwitchA] = "External", [SwitchB] = "Lab" };

    [Fact]
    public void Adapters_KeepsAnAdapterWithNoConnection()
    {
        // Seen live: a copy whose adapter is on no switch has no connection object, and the list built from the
        // connections had no entry for it, so moving that copy on failed at the import.
        var adapters = CimHyperVDefinitionReader.Adapters(
            [new(@"Microsoft:VM\Port1", "000000000000")],
            [],
            Switches);

        var adapter = Assert.Single(adapters);
        Assert.Null(adapter.SwitchId);
        Assert.Null(adapter.SwitchName);
        Assert.Equal("000000000000", adapter.MacAddress);
    }

    [Fact]
    public void Adapters_PairsEachAdapterWithItsOwnConnection_WhateverTheirOrder()
    {
        var adapters = CimHyperVDefinitionReader.Adapters(
            [new(@"Microsoft:VM\Port1", "00155D000001"), new(@"Microsoft:VM\Port2", "00155D000002"), new(@"Microsoft:VM\Port3", "00155D000003")],
            [new(PortPath(@"Microsoft:VM\Port3"), SwitchPath(SwitchB)), new(PortPath(@"Microsoft:VM\Port1"), SwitchPath(SwitchA))],
            Switches);

        Assert.Equal(
            [
                new VmAdapterDefinition(SwitchA, "External", "00155D000001"),
                new VmAdapterDefinition(null, null, "00155D000002"),
                new VmAdapterDefinition(SwitchB, "Lab", "00155D000003"),
            ],
            adapters);
    }

    [Fact]
    public void Adapters_ReadsAConnectionWithAnEmptySwitch_AsNotConnected()
    {
        var adapters = CimHyperVDefinitionReader.Adapters(
            [new(@"Microsoft:VM\Port1", "00155D000001")],
            [new(PortPath(@"Microsoft:VM\Port1"), null), new(PortPath(@"Microsoft:VM\Port9"), SwitchPath(SwitchB))],
            Switches);

        Assert.Equal([new VmAdapterDefinition(null, null, "00155D000001")], adapters);
    }
}
