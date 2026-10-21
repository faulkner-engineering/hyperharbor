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
}
