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

public sealed class PerformanceApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public PerformanceApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Off));
        _host.Compute.States[VmId] = new ComputeState(2, 4096, 8192, true, false, false, 1);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    private static object Settings() => new { processorCount = 4, memoryMb = 4096, gpu = new { vramPercent = 60 }, rdp = new { hardwareEncoding = true } };

    [Fact]
    public async Task Apply_NeedsElevation()
    {
        var response = await _client.PutAsJsonAsync($"/api/v1/vms/{VmId}/performance", Settings());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Performance.Calls);
    }

    [Fact]
    public async Task Apply_StartsAJob_AndTheVmReportsPerformanceMode()
    {
        var response = await SendElevated(HttpMethod.Put, $"/api/v1/vms/{VmId}/performance", Settings());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("applyPerformance", (string?)job["kind"]);
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] == "succeeded");

        var performance = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/performance"))!;
        Assert.True((bool?)performance["enabled"]);
        Assert.True((bool?)performance["gpuAttached"]);
        Assert.Equal(60, (int?)performance["settings"]!["gpu"]!["vramPercent"]);
        Assert.True((bool?)performance["settings"]!["rdp"]!["hardwareEncoding"]);
        Assert.True((bool?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}"))!["performanceMode"]);

        var audit = _host.AuditEntries().Last(entry => (string?)entry["action"] == "applyVmPerformance");
        Assert.Contains("processors=4, memoryMb=4096, gpu=60/50/50/50%", (string?)audit["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ToARunningVm_Returns409VmMustBeOff()
    {
        _host.Inventory.Vms[0] = _host.Inventory.Vms[0] with { State = VmState.Running };

        var response = await SendElevated(HttpMethod.Put, $"/api/v1/vms/{VmId}/performance", Settings());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.VmMustBeOff, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
        Assert.Empty(_host.Performance.Calls);
    }

    [Fact]
    public async Task Remove_NeedsElevation_AndTurnsItOff()
    {
        var applied = await SendElevated(HttpMethod.Put, $"/api/v1/vms/{VmId}/performance", Settings());
        var job = (await applied.Content.ReadFromJsonAsync<JsonObject>())!;
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] == "succeeded");

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.DeleteAsync($"/api/v1/vms/{VmId}/performance")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SendElevated(HttpMethod.Delete, $"/api/v1/vms/{VmId}/performance", null)).StatusCode);
        Assert.False((bool?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/performance"))!["enabled"]);
    }

    [Fact]
    public async Task HostGpu_ListsThePartitionableGpu()
    {
        var gpu = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/gpu"))!;

        var device = gpu["gpus"]![0]!;
        Assert.Equal("intel", (string?)device["vendor"]);
        Assert.True((bool?)device["partitionable"]);
        Assert.Equal(32, (int?)device["partitionCount"]);
        Assert.Empty(gpu["warnings"]!.AsArray());
    }

    [Fact]
    public async Task HostGpu_ReportsRecentDriverErrors()
    {
        using var host = new TestHost();
        host.Pair(_certificate, "Laptop");
        using var client = host.CreateClient(_certificate);
        host.GpuEvents.Events.Add(new Core.Performance.GpuEvent("Display", 4101, DateTimeOffset.UtcNow.AddHours(-1), null));

        var warning = (await client.GetFromJsonAsync<JsonObject>("/api/v1/host/gpu"))!["warnings"]![0]!;

        Assert.Equal("Display", (string?)warning["provider"]);
        Assert.Equal(4101, (int?)warning["eventId"]);
        Assert.Equal(1, (int?)warning["count"]);
    }

    [Fact]
    public async Task GuestSetup_NeedsElevation_AndACredential()
    {
        var applied = await SendElevated(HttpMethod.Put, $"/api/v1/vms/{VmId}/performance", Settings());
        var job = (await applied.Content.ReadFromJsonAsync<JsonObject>())!;
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{job["id"]}"))!["state"] == "succeeded");
        _host.Inventory.Vms[0] = _host.Inventory.Vms[0] with { State = VmState.Running };

        Assert.Equal(HttpStatusCode.Forbidden, (await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/performance/guest-setup", new { driversOnly = false })).StatusCode);

        var refused = await SendElevated(HttpMethod.Post, $"/api/v1/vms/{VmId}/performance/guest-setup", new { driversOnly = false });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.CredentialRequired, (string?)(await refused.Content.ReadFromJsonAsync<JsonObject>())!["code"]);

        _host.Services.GetRequiredService<Core.Provisioning.VmCredentialStore>().Save(VmId, new Core.Provisioning.GuestCredential("hhadmin", "Admin-Pass1!"));
        var started = await SendElevated(HttpMethod.Post, $"/api/v1/vms/{VmId}/performance/guest-setup", new { driversOnly = false });
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var setup = (await started.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("performanceGuestSetup", (string?)setup["kind"]);
        await WaitUntil(async () => (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{setup["id"]}"))!["state"] == "succeeded");
        Assert.Single(_host.GuestSetup.Runs);
        Assert.Equal("30.0.101.1122", (string?)(await _client.GetFromJsonAsync<JsonObject>($"/api/v1/vms/{VmId}/performance"))!["driver"]!["guestVersion"]);
    }

    private async Task<HttpResponseMessage> SendElevated(HttpMethod method, string path, object? body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(50);
        }
    }
}
