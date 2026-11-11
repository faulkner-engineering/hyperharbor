using HyperHarbor.Host.Tray.Api;

namespace HyperHarbor.Host.Tests.Tray;

/// <summary>Which API requests the tray window may send through the tray's device (phase 33).</summary>
public sealed class TrayApiRoutesTests
{
    private const string Vm = "0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b";

    [Theory]
    [InlineData("GET", "/vms")]
    [InlineData("POST", "/vms/" + Vm + "/actions")]
    [InlineData("GET", "/vms/" + Vm + "/compute")]
    [InlineData("PATCH", "/vms/" + Vm + "/compute")]
    [InlineData("GET", "/vms/" + Vm + "/disks")]
    [InlineData("POST", "/vms/" + Vm + "/disks/grow")]
    [InlineData("GET", "/vms/" + Vm + "/delete-preview")]
    [InlineData("POST", "/vms/" + Vm + "/delete")]
    [InlineData("GET", "/host/resources")]
    [InlineData("GET", "/switches")]
    [InlineData("GET", "/auth/elevation")]
    [InlineData("POST", "/auth/elevation")]
    [InlineData("GET", "/jobs/" + Vm)]
    public void TheVmPagesRoutes_AreAllowed(string method, string path) =>
        Assert.True(TrayApiRoutes.IsAllowed(method, path));

    [Theory]
    // Routes the first version of the page does not use.
    [InlineData("POST", "/vms")]
    [InlineData("GET", "/pairing/devices")]
    [InlineData("DELETE", "/auth/elevation")]
    [InlineData("POST", "/vms/" + Vm + "/connect")]
    [InlineData("PUT", "/isos/a.iso")]
    // The right path with another method.
    [InlineData("DELETE", "/vms")]
    [InlineData("get", "/vms")]
    // Paths that try to reach another route through the ID or the shape of the path.
    [InlineData("GET", "/vms/../pairing/devices/compute")]
    [InlineData("GET", "/vms/" + Vm + "/compute/")]
    [InlineData("GET", "/vms/not-a-guid/compute")]
    [InlineData("GET", "/vms/" + Vm + "%2F..%2Fx/compute")]
    [InlineData("GET", "/vms?state=running")]
    [InlineData("GET", "vms")]
    [InlineData("GET", "//vms")]
    [InlineData("GET", "")]
    [InlineData("GET", "https://example.com/vms")]
    public void OtherRequests_AreRefused(string method, string path) =>
        Assert.False(TrayApiRoutes.IsAllowed(method, path));
}
