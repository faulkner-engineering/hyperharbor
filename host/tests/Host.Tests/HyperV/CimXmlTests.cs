using System.Xml.Linq;
using HyperHarbor.Host.Core.HyperV;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Tests.HyperV;

public class CimXmlTests
{
    [Fact]
    public void Write_ProducesCimXmlInstances_WithEscapedValues()
    {
        var xml = CimXml.Write("Msvm_VirtualHardDiskSettingData",
        [
            new CimXml.Property("Path", CimType.String, @"C:\VMs\A & B\Disk.vhdx"),
            new CimXml.Property("MaxInternalSize", CimType.UInt64, 64UL * 1024 * 1024 * 1024),
            new CimXml.Property("Type", CimType.UInt16, (ushort)3),
            new CimXml.Property("Enabled", CimType.Boolean, true),
            new CimXml.Property("HostResource", CimType.StringArray, new[] { @"C:\ISO\setup.iso" }),
            new CimXml.Property("Unset", CimType.String, null),
        ]);

        var root = XElement.Parse(xml);
        Assert.Equal("INSTANCE", root.Name.LocalName);
        Assert.Equal("Msvm_VirtualHardDiskSettingData", root.Attribute("CLASSNAME")?.Value);
        Assert.Equal(@"C:\VMs\A & B\Disk.vhdx", Value(root, "Path"));
        Assert.Equal("68719476736", Value(root, "MaxInternalSize"));
        Assert.Equal("uint64", Property(root, "MaxInternalSize").Attribute("TYPE")?.Value);
        Assert.Equal("true", Value(root, "Enabled"));
        Assert.Null(Property(root, "Unset").Element("VALUE"));
        var array = root.Elements("PROPERTY.ARRAY").Single(element => element.Attribute("NAME")?.Value == "HostResource");
        Assert.Equal("string", array.Attribute("TYPE")?.Value);
        Assert.Equal([@"C:\ISO\setup.iso"], array.Descendants("VALUE").Select(value => value.Value));
    }

    [Fact]
    public void ReadScalars_ReadsWhatHyperVReturns()
    {
        const string xml = """
            <INSTANCE CLASSNAME="Msvm_VirtualHardDiskSettingData">
              <PROPERTY NAME="Path" TYPE="string"><VALUE>C:\VMs\Child.avhdx</VALUE></PROPERTY>
              <PROPERTY NAME="ParentPath" TYPE="string"><VALUE>C:\VMs\Base.vhdx</VALUE></PROPERTY>
              <PROPERTY NAME="Format" TYPE="uint16"><VALUE>3</VALUE></PROPERTY>
              <PROPERTY NAME="Empty" TYPE="string"></PROPERTY>
            </INSTANCE>
            """;

        var values = CimXml.ReadScalars(xml);

        Assert.Equal(@"C:\VMs\Base.vhdx", values["parentpath"]);
        Assert.Equal("3", values["Format"]);
        Assert.False(values.ContainsKey("Empty"));
    }

    [Fact]
    public void WrittenInstances_ReadBack()
    {
        var xml = CimXml.Write("Msvm_Test", [new CimXml.Property("Name", CimType.String, "<Dev>")]);

        Assert.Equal("<Dev>", CimXml.ReadScalars(xml)["Name"]);
    }

    private static XElement Property(XElement root, string name) =>
        root.Elements("PROPERTY").Single(element => element.Attribute("NAME")?.Value == name);

    private static string? Value(XElement root, string name) => Property(root, name).Element("VALUE")?.Value;
}
