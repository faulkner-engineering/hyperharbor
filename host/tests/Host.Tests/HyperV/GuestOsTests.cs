using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.HyperV;

public class GuestOsTests
{
    private static readonly Guid VmId = Guid.Parse("62671de3-fe20-48d6-a7c4-fced2c5fd6f4");

    private static string Item(string name, string data) =>
        $"""<INSTANCE CLASSNAME="Msvm_KvpExchangeDataItem"><PROPERTY NAME="Caption" TYPE="string"></PROPERTY><PROPERTY NAME="Data" TYPE="string"><VALUE>{data}</VALUE></PROPERTY><PROPERTY NAME="Name" TYPE="string"><VALUE>{name}</VALUE></PROPERTY><PROPERTY NAME="Source" TYPE="uint16"><VALUE>2</VALUE></PROPERTY></INSTANCE>""";

    [Fact]
    public void KvpItems_ParsesNameAndData_AndSkipsMalformedItems()
    {
        var values = KvpItems.Parse([Item("OSName", "Ubuntu"), "<not xml", Item("OSMajorVersion", "24.04")]);

        Assert.Equal("Ubuntu", values["OSName"]);
        Assert.Equal("24.04", values["osmajorversion"]);
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void LinuxGuest_ObservedOnHyperHarborLinux_IsLinuxWithVersion()
    {
        // Values reported by hv_kvp_daemon on Ubuntu 24.04 (HyperHarbor-Linux, 2026-10-02).
        var row = KvpItems.ToGuestOsRow(VmId, [Item("OSName", "Ubuntu"), Item("OSMajorVersion", "24.04"), Item("OSPlatformId", "129")]);

        Assert.Equal(new VmGuestOs(GuestOsFamily.Linux, "Ubuntu 24.04"), VmMapper.MapGuestOs(row));
    }

    [Theory]
    [InlineData("Windows 11 Pro", "10", "2")]
    [InlineData("Windows Server 2022 Datacenter", "10", null)]
    [InlineData("Some Future Name", "10", "2")]
    public void WindowsGuest_IsWindows(string name, string major, string? platform)
    {
        var row = new GuestOsRow(VmId, name, major, platform is null ? null : int.Parse(platform));

        var guest = VmMapper.MapGuestOs(row);

        Assert.Equal(GuestOsFamily.Windows, guest.Family);
        Assert.Equal(name, guest.Name);
    }

    [Fact]
    public void MissingOrBsdGuest_IsUnknown()
    {
        Assert.Equal(VmGuestOs.Unknown, VmMapper.MapGuestOs(null));
        Assert.Equal(VmGuestOs.Unknown, VmMapper.MapGuestOs(new GuestOsRow(VmId, null, null, null)));
        Assert.Equal(GuestOsFamily.Unknown, VmMapper.MapGuestOs(new GuestOsRow(VmId, "FreeBSD", "14", null)).Family);
    }

    [Fact]
    public void Map_ReportsGuestOsOnlyForActiveVms()
    {
        var running = Guid.NewGuid();
        var off = Guid.NewGuid();
        var snapshot = new HyperVSnapshot(
            [new ComputerSystemRow(running, "Linux", 2, 1000), new ComputerSystemRow(off, "Old", 3, 0)],
            [],
            [],
            [],
            [new GuestOsRow(running, "Ubuntu", "24.04", 129), new GuestOsRow(off, "Windows 10 Pro", "10", 2)]);

        var vms = VmMapper.Map(snapshot).ToDictionary(vm => vm.Id);

        Assert.Equal(GuestOsFamily.Linux, vms[running].GuestOs!.Family);
        Assert.Equal(VmGuestOs.Unknown, vms[off].GuestOs);
    }
}
