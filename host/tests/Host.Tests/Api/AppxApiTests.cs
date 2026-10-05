using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Core.Provisioning;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class AppxApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("4f6d7a1e-2b3c-4d5e-8f90-a1b2c3d4e5f6");
    private const string AdminPassword = "Admin-Pass1!";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public AppxApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Win11", VmState.Running));
        _host.Services.GetRequiredService<VmCredentialStore>().Save(VmId, new GuestCredential("hhadmin", AdminPassword));
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task WithoutABaseline_PackagesAreRated_AndNotCompared()
    {
        var inventory = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/appx"))!;

        Assert.Equal("10.0.26100.2033", (string?)inventory["build"]);
        Assert.Null(inventory["baseline"]);
        var news = Package(inventory["packages"]!, "Microsoft.BingNews");
        Assert.Equal(("Microsoft News", "Microsoft", "safe"), ((string?)news["friendlyName"], (string?)news["publisher"], (string?)news["rating"]));
        Assert.Null(news["inBaseline"]);
        Assert.Equal("keep", (string?)Package(inventory["packages"]!, "Microsoft.WindowsStore")["rating"]);
        Assert.Equal(AdminPassword, Assert.Single(_host.ProfileReader.AdminsUsed).Password);
    }

    [Fact]
    public async Task ARecordedBaseline_ShowsWhatWasRemovedAndAdded()
    {
        var recorded = await _client.PostAsync($"/api/v1/vms/{VmId}/appx-baseline", null);
        Assert.Equal(HttpStatusCode.OK, recorded.StatusCode);
        Assert.Equal("10.0.26100/Professional", (string?)(await recorded.Content.ReadFromJsonAsync<JsonObject>())!["key"]);

        _host.ProfileReader.Appx = ["Microsoft.WindowsStore", "Microsoft.GamingApp", "Contoso.Extra"];
        var inventory = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/appx"))!;

        Assert.Equal("manual", (string?)inventory["baseline"]!["source"]);
        Assert.False((bool)inventory["baseline"]!["approximate"]!);
        Assert.Equal(["Clipchamp.Clipchamp", "Microsoft.BingNews"], inventory["removedFromBaseline"]!.AsArray().Select(package => (string)package!["name"]!));
        Assert.False((bool)Package(inventory["packages"]!, "Contoso.Extra")["inBaseline"]!);
        Assert.Equal("Contoso", (string?)Package(inventory["packages"]!, "Contoso.Extra")["publisher"]);
        Assert.Contains(_host.AuditEntries(), entry => (string?)entry["action"] == "recordAppxBaseline");
    }

    [Fact]
    public async Task AStoppedVm_OrOneWithoutACredential_Is409WithACode()
    {
        _host.Services.GetRequiredService<VmCredentialStore>().Remove(VmId);
        var noCredential = await _client.GetAsync($"/api/v1/vms/{VmId}/appx");
        _host.Inventory.Vms[0] = _host.Inventory.Vms[0] with { State = VmState.Off };
        var off = await _client.GetAsync($"/api/v1/vms/{VmId}/appx");

        Assert.Equal(ContractInfo.ProblemCodes.CredentialRequired, await CodeAsync(noCredential));
        Assert.Equal(ContractInfo.ProblemCodes.VmNotRunning, await CodeAsync(off));
    }

    [Fact]
    public async Task GuestErrors_NeverCarryTheAdminPassword()
    {
        _host.ProfileReader.Failure = new GuestOperationException($"Access denied for hhadmin with {AdminPassword}");

        var response = await _client.GetAsync($"/api/v1/vms/{VmId}/appx");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain(AdminPassword, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        _host.Logs.AssertNoneContain(AdminPassword);
    }

    [Fact]
    public async Task AnUnknownSource_Is400()
    {
        var response = await _client.GetAsync($"/api/v1/vms/{VmId}/appx?source=golden");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static JsonNode Package(JsonNode packages, string name) =>
        packages.AsArray().Single(package => (string?)package!["name"] == name)!;

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"];
}
