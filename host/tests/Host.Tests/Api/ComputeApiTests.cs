using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class ComputeApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public ComputeApiTests()
    {
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _host.Compute.States[VmId] = new ComputeState(2, 4096, 4096, false, false, false, 1);
        _host.Pair(_certificate);
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Get_ReturnsSettingsAndRequiresOff()
    {
        var settings = await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/compute");

        Assert.Equal(2, (int?)settings!["processorCount"]);
        var requiresOff = settings["requiresOff"]!.AsArray().Select(item => (string?)item).ToList();
        Assert.Contains("processorCount", requiresOff);
        Assert.DoesNotContain("macAddressSpoofing", requiresOff);
    }

    [Fact]
    public async Task Patch_WithoutElevation_Returns403()
    {
        var response = await _client.PatchAsJsonAsync($"/api/v1/vms/{VmId}/compute", new { processorCount = 4 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Compute.Applied);
    }

    [Fact]
    public async Task Patch_OffVm_Returns200WithTheNewSettings()
    {
        var response = await SendElevated(new { processorCount = 4, nestedVirtualization = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(4, (int?)body["settings"]!["processorCount"]);
        Assert.Null(body["job"]);
    }

    [Fact]
    public async Task Patch_RunningVm_NeedingOff_Returns409RequiresShutdown()
    {
        _host.Inventory.SetState(VmId, VmState.Running);

        var response = await SendElevated(new { processorCount = 4 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.RequiresShutdown, (string?)problem["code"]);
    }

    [Fact]
    public async Task Patch_ShutDownToApply_Returns202WithAJob()
    {
        _host.Inventory.SetState(VmId, VmState.Running);
        _host.Invoker.ResultingState = VmState.Off;

        var response = await SendElevated(new { processorCount = 4, shutDownToApply = true });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Null(body["settings"]);
        Assert.Equal("applyCompute", (string?)body["job"]!["kind"]);
        Assert.Equal($"/api/v1/jobs/{(string?)body["job"]!["id"]}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Patch_NestedWithDynamicMemory_Returns400()
    {
        var response = await SendElevated(new { nestedVirtualization = true, dynamicMemory = true, maximumMemoryMb = 8192 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendElevated(object body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/vms/{VmId}/compute") { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }
}
