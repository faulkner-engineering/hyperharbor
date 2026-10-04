using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Api;

public sealed class RemoteDesktopApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly HttpClient _client;

    public RemoteDesktopApiTests()
    {
        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate, "Living Room Laptop");
        _client = _host.CreateClient(certificate);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Windows 11 Dev", VmState.Running) with { IpAddresses = ["192.168.0.50"] });
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Theory]
    [InlineData("/provision")]
    [InlineData("/connect")]
    public async Task Endpoints_RequirePairing(string suffix)
    {
        using var anonymous = _host.CreateClient();

        var response = await anonymous.PostAsJsonAsync($"/api/v1/vms/{VmId}{suffix}", new { adminUserName = "a", adminPassword = "b" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ProvisionThenConnect_ReturnsCredentialsWithoutCaching()
    {
        var provision = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "Administrator", adminPassword = "Adm1n!" });
        Assert.Equal(HttpStatusCode.OK, provision.StatusCode);
        Assert.Equal("hh-owner", (string?)(await provision.Content.ReadFromJsonAsync<JsonObject>())!["accountName"]);

        var vm = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/vms"))!.Single()!;
        Assert.True((bool?)vm["provisioned"]);
        Assert.Equal("192.168.0.50", (string?)vm["remoteDesktop"]!["address"]);

        var connect = await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null);
        Assert.Equal(HttpStatusCode.OK, connect.StatusCode);
        Assert.True(connect.Headers.CacheControl?.NoStore);
        var body = (await connect.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("hh-owner", (string?)body["userName"]);
        Assert.Equal("192.168.0.50", (string?)body["address"]);
        Assert.Equal(3389, (int?)body["port"]);
        Assert.Equal(_host.Guest.PasswordsSet.Last().Password, (string?)body["password"]);
    }

    [Fact]
    public async Task ConnectTwiceWithinWindow_ReusesPassword()
    {
        await Provision();

        var first = await (await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null)).Content.ReadFromJsonAsync<JsonObject>();
        var second = await (await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null)).Content.ReadFromJsonAsync<JsonObject>();

        Assert.Equal((string?)first!["password"], (string?)second!["password"]);
    }

    [Fact]
    public async Task Connect_NotProvisioned_Returns409()
    {
        var response = await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Connect_VmStopped_Returns409()
    {
        await Provision();
        _host.Inventory.SetState(VmId, VmState.Off);

        Assert.Equal(HttpStatusCode.Conflict, (await _client.PostAsync($"/api/v1/vms/{VmId}/connect", null)).StatusCode);
    }

    [Theory]
    [InlineData(typeof(GuestCredentialRejectedException), HttpStatusCode.UnprocessableEntity)]
    [InlineData(typeof(GuestAccountConflictException), HttpStatusCode.Conflict)]
    [InlineData(typeof(GuestUnavailableException), HttpStatusCode.ServiceUnavailable)]
    [InlineData(typeof(GuestOperationException), HttpStatusCode.BadGateway)]
    public async Task Provision_GuestErrors_MapToStatusCodes(Type errorType, HttpStatusCode expected)
    {
        _host.Guest.Failure = (Exception)Activator.CreateInstance(errorType, "guest said no")!;

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "Administrator", adminPassword = "Adm1n!" });

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Adm1n!", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Provision_MissingPassword_Returns400()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "Administrator" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task Provision()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/provision", new { adminUserName = "Administrator", adminPassword = "Adm1n!" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
