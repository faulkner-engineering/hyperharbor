using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class GameAwareApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public GameAwareApiTests()
    {
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running));
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    private object Settings(bool enabled = true) => new
    {
        enabled,
        defaultAction = "pause",
        allowlist = new[] { "Emulator.exe", "powerpnt" },
        ignoreList = new[] { "POWERPNT.EXE" },
        vmPolicies = new[] { new { vmId = VmId, action = "keep", alwaysPause = true } },
    };

    [Fact]
    public async Task Get_StartsOff_AndListsTheVms()
    {
        var info = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/game-aware"))!;

        Assert.False((bool?)info["settings"]!["enabled"]);
        Assert.Equal("pause", (string?)info["settings"]!["defaultAction"]);
        Assert.False((bool?)info["state"]!["gaming"]);
        Assert.Equal("Dev Box", (string?)info["vms"]![0]!["name"]);
        Assert.False((bool?)info["vms"]![0]!["gpuPartition"]);
    }

    [Fact]
    public async Task Put_NeedsElevation()
    {
        var response = await _client.PutAsJsonAsync("/api/v1/host/game-aware", Settings());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False((bool?)(await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/game-aware"))!["settings"]!["enabled"]);
    }

    [Fact]
    public async Task Put_StoresNormalizedLists_ThePolicy_AndAuditsWithoutProgramNames()
    {
        var response = await SendElevated(HttpMethod.Put, "/api/v1/host/game-aware", Settings());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var stored = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/game-aware"))!["settings"]!;
        Assert.True((bool?)stored["enabled"]);
        Assert.Equal(["emulator"], stored["allowlist"]!.AsArray().Select(n => (string)n!).ToArray());
        Assert.Equal(["powerpnt"], stored["ignoreList"]!.AsArray().Select(n => (string)n!).ToArray());
        var policy = stored["vmPolicies"]![0]!;
        Assert.Equal("keep", (string?)policy["action"]);
        Assert.True((bool?)policy["alwaysPause"]);
        var audit = _host.AuditEntries().Last(entry => (string?)entry["action"] == "setGameAware");
        Assert.Contains("enabled=True", (string?)audit["detail"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Put_WithADuplicateVm_Returns400()
    {
        var body = new
        {
            enabled = true,
            defaultAction = "pause",
            allowlist = Array.Empty<string>(),
            ignoreList = Array.Empty<string>(),
            vmPolicies = new[] { new { vmId = VmId, action = "save" }, new { vmId = VmId, action = "pause" } },
        };

        var response = await SendElevated(HttpMethod.Put, "/api/v1/host/game-aware", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NotAGame_WithNoGameActive_ChangesNothing()
    {
        var response = await _client.PostAsync("/api/v1/host/game-aware/not-a-game", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await response.Content.ReadFromJsonAsync<JsonObject>())!["ignored"]);
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
}
