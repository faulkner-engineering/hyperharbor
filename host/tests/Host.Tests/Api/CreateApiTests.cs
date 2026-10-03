using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class CreateApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public CreateApiTests()
    {
        Directory.CreateDirectory(_host.IsoFolder);
        File.WriteAllBytes(Path.Combine(_host.IsoFolder, "win11.iso"), new byte[1]);
        _host.Pair(_certificate, "Laptop");
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
    public async Task Create_WithoutElevation_Returns403()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/vms", Request());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_host.Builder.Steps);
    }

    [Fact]
    public async Task Create_StartsAJob_AndAuditsTheNewVm()
    {
        var response = await SendElevated(Request());

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("createVm", (string?)job["kind"]);
        var finished = await WaitForJobAsync((string)job["id"]!);
        Assert.Equal("succeeded", (string?)finished["state"]);
        Assert.Equal(_host.Builder.CreatedVmId.ToString(), (string?)finished["vmId"]);

        await WaitUntil(() => _host.AuditEntries().Count(entry => (string?)entry["action"] == "createVm") == 3);
        var last = _host.AuditEntries().Last(entry => (string?)entry["action"] == "createVm");
        Assert.Equal("Win11 Dev", (string?)last["vmName"]);
        Assert.Equal(_host.Builder.CreatedVmId.ToString(), (string?)last["vmId"]);
    }

    [Fact]
    public async Task InvalidRequest_Returns400_WithEveryError()
    {
        var response = await SendElevated(Request() with { name = "", processorCount = 99 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        var fields = problem["errors"]!.AsArray().Select(error => (string?)error!["field"]).ToList();
        Assert.Contains("name", fields);
        Assert.Contains("processorCount", fields);
    }

    [Fact]
    public async Task ResourceWarnings_Return409_UntilAcknowledged()
    {
        _host.Capacity.Capacity = new HostCapacity(16, 32768, 5000);

        var warned = await SendElevated(Request());
        Assert.Equal(HttpStatusCode.Conflict, warned.StatusCode);
        var problem = (await warned.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(ContractInfo.ProblemCodes.ResourceWarnings, (string?)problem["code"]);
        Assert.Equal("startupMemoryMb", (string?)problem["warnings"]![0]!["field"]);
        Assert.Empty(_host.Builder.Steps);

        var accepted = await SendElevated(Request() with { acknowledgeWarnings = true });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task MissingRequiredFields_Return400()
    {
        var response = await SendElevated(new { name = "Win11 Dev" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Body Request() => new("Win11 Dev", "win11.iso", 64, 4, 4096, 8192, false, null, true, false);

    /// <summary>The request as JSON properties, so tests can send values the C# record would not allow.</summary>
    private sealed record Body(
        string name,
        string isoName,
        int diskSizeGb,
        int processorCount,
        long startupMemoryMb,
        long maximumMemoryMb,
        bool dynamicMemory,
        string? switchId,
        bool enableTpm,
        bool acknowledgeWarnings);

    private async Task<HttpResponseMessage> SendElevated(object body)
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/vms") { Content = JsonContent.Create(body, body.GetType()) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }

    private async Task<JsonObject> WaitForJobAsync(string jobId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var job = (await _client.GetFromJsonAsync<JsonObject>($"/api/v1/jobs/{jobId}"))!;
            if ((string?)job["state"] != "running")
            {
                return job;
            }

            Assert.True(DateTime.UtcNow < deadline, "The job did not finish.");
            await Task.Delay(20);
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(20);
        }
    }
}
