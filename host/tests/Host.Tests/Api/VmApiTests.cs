using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Api;

public sealed class VmApiTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly FakeVmInventory _inventory;
    private readonly FakePowerInvoker _invoker;
    private readonly HttpClient _client;

    public VmApiTests()
    {
        _inventory = _host.Inventory;
        _invoker = _host.Invoker;
        using var certificate = TestHost.CreateClientCertificate();
        _host.Pair(certificate);
        _client = _host.CreateClient(certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task ListVms_ReturnsContractJson()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Running));

        var response = await _client.GetAsync("/api/v1/vms");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();
        var vm = Assert.Single(body)!;
        Assert.Equal(VmId.ToString(), (string?)vm["id"]);
        Assert.Equal("running", (string?)vm["state"]);
    }

    [Fact]
    public async Task GetVm_Unknown_Returns404Problem()
    {
        var response = await _client.GetAsync($"/api/v1/vms/{Guid.NewGuid()}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetVm_NonGuidId_Returns404()
    {
        var response = await _client.GetAsync("/api/v1/vms/not-a-guid");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PerformAction_Accepted_Returns202WithLocationAndResult()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _invoker.ResultingState = VmState.Starting;

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "start" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/v1/vms/{VmId}", response.Headers.Location?.OriginalString);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("start", (string?)body["action"]);
        Assert.Equal("starting", (string?)body["state"]);
        Assert.True((bool?)body["accepted"]);
        Assert.Equal([(VmId, VmAction.Start)], _invoker.Calls);
    }

    [Fact]
    public async Task PerformAction_InvalidForState_Returns409()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "turnOff" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Empty(_invoker.Calls);
    }

    [Fact]
    public async Task PerformAction_UnknownVm_Returns404()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "start" });

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"action":"explode"}""")]
    [InlineData("""{"action":0}""")]
    [InlineData("not json")]
    public async Task PerformAction_InvalidBody_Returns400WithoutInvoking(string body)
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));

        var response = await _client.PostAsync(
            $"/api/v1/vms/{VmId}/actions",
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_invoker.Calls);
    }

    [Fact]
    public async Task HyperVUnavailable_Returns503Problem()
    {
        _inventory.ThrowOnRead = new HyperVUnavailableException("Hyper-V is not enabled.");

        var response = await _client.GetAsync("/api/v1/vms");

        await AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HyperVOperationFailure_Returns502Problem()
    {
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Running));
        _invoker.ThrowOnInvoke = new HyperVOperationException("RequestStateChange", 32768);

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "save" });

        await AssertProblemAsync(response, HttpStatusCode.BadGateway);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal((int)expected, (int?)problem["status"]);
    }
}
