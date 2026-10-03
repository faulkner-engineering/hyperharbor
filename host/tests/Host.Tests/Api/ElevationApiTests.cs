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

public sealed class ElevationApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public ElevationApiTests()
    {
        _host.Inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev Box", VmState.Running));
        _host.Pair(_certificate, "Laptop");
        _client = _host.CreateClient(_certificate);
    }

    public void Dispose()
    {
        _client.Dispose();
        _certificate.Dispose();
        _host.Dispose();
    }

    [Fact]
    public async Task Status_BeforeAndAfterElevating()
    {
        var before = await _client.GetFromJsonAsync<JsonObject>("/api/v1/auth/elevation");
        Assert.False((bool?)before!["configured"]);
        Assert.False((bool?)before["active"]);
        Assert.True(before.ContainsKey("expiresAt"));
        Assert.Null(before["expiresAt"]);

        SetPassphrase();
        await ElevateAsync();

        var after = await _client.GetFromJsonAsync<JsonObject>("/api/v1/auth/elevation");
        Assert.True((bool?)after!["configured"]);
        Assert.True((bool?)after["active"]);
        Assert.NotNull((string?)after["expiresAt"]);
    }

    [Fact]
    public async Task Elevate_ReturnsATokenThatIsNotCached()
    {
        SetPassphrase();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(string.IsNullOrEmpty((string?)body!["token"]));
        Assert.NotNull((string?)body["expiresAt"]);
    }

    [Fact]
    public async Task Elevate_WithAWrongPassphrase_Returns403IncorrectPassphrase()
    {
        SetPassphrase();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = "wrong passphrase" });

        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, ContractInfo.ProblemCodes.IncorrectPassphrase);
    }

    [Fact]
    public async Task Elevate_WithoutAPassphraseSet_Returns403ElevationUnavailable()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });

        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, ContractInfo.ProblemCodes.ElevationUnavailable);
    }

    [Fact]
    public async Task Elevate_WithoutAPassphraseInTheBody_Returns400()
    {
        SetPassphrase();

        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RepeatedWrongPassphrases_Return429WithRetryAfter()
    {
        SetPassphrase();
        for (var attempt = 0; attempt < ElevationService.MaxFailuresPerDevice; attempt++)
        {
            await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = "wrong passphrase" });
        }

        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });

        await AssertProblemCodeAsync(response, (HttpStatusCode)429, ContractInfo.ProblemCodes.TooManyAttempts);
        Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task TurnOff_WithoutElevation_Returns403_AndIsNotSent()
    {
        SetPassphrase();

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "turnOff" });

        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, ContractInfo.ProblemCodes.ElevationRequired);
        Assert.Empty(_host.Invoker.Calls);
        var failed = _host.AuditEntries()[^1];
        Assert.Equal("failed", (string?)failed["outcome"]);
        Assert.Equal(403, (int?)failed["status"]);
        Assert.False((bool?)failed["elevated"]);
    }

    [Fact]
    public async Task TurnOff_WithoutAPassphraseSet_Returns403ElevationUnavailable()
    {
        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "turnOff" });

        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, ContractInfo.ProblemCodes.ElevationUnavailable);
        Assert.Empty(_host.Invoker.Calls);
    }

    [Fact]
    public async Task TurnOff_WithElevation_IsSent_AndAuditedAsElevated()
    {
        SetPassphrase();
        var token = await ElevateAsync();

        var response = await SendWithToken(HttpMethod.Post, $"/api/v1/vms/{VmId}/actions", token, new { action = "turnOff" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([(VmId, VmAction.TurnOff)], _host.Invoker.Calls);
        var succeeded = _host.AuditEntries()[^1];
        Assert.Equal("succeeded", (string?)succeeded["outcome"]);
        Assert.True((bool?)succeeded["elevated"]);
    }

    [Fact]
    public async Task OtherPowerActions_DoNotNeedElevation()
    {
        SetPassphrase();

        var response = await _client.PostAsJsonAsync($"/api/v1/vms/{VmId}/actions", new { action = "shutdown" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task AnotherDevicesToken_IsRejected()
    {
        SetPassphrase();
        var token = await ElevateAsync();
        using var otherCertificate = TestHost.CreateClientCertificate("Other");
        _host.Pair(otherCertificate, "Phone");
        using var other = _host.CreateClient(otherCertificate);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/vms/{VmId}/actions") { Content = JsonContent.Create(new { action = "turnOff" }) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        var response = await other.SendAsync(request);

        await AssertProblemCodeAsync(response, HttpStatusCode.Forbidden, ContractInfo.ProblemCodes.ElevationRequired);
        Assert.Empty(_host.Invoker.Calls);
    }

    [Fact]
    public async Task DroppedElevation_NoLongerWorks()
    {
        SetPassphrase();
        var token = await ElevateAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/v1/auth/elevation")).StatusCode);

        var response = await SendWithToken(HttpMethod.Post, $"/api/v1/vms/{VmId}/actions", token, new { action = "turnOff" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ElevationAttempts_AreAudited_WithoutThePassphrase()
    {
        SetPassphrase();

        await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = "wrong passphrase" });
        var token = await ElevateAsync();

        var outcomes = _host.AuditEntries()
            .Where(entry => (string?)entry["action"] == "elevate")
            .Select(entry => (string?)entry["outcome"]);
        Assert.Equal(["requested", "failed", "requested", "succeeded"], outcomes);
        var audit = File.ReadAllText(Path.Combine(_host.DataDirectory, Core.Audit.FileAuditLog.FileName));
        Assert.DoesNotContain("wrong passphrase", audit, StringComparison.Ordinal);
        Assert.DoesNotContain(Passphrase, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(token, audit, StringComparison.Ordinal);
        _host.Logs.AssertNoneContain(Passphrase, "wrong passphrase", token);
    }

    private void SetPassphrase()
    {
        var hash = AdminPassphrase.CreateHash(Passphrase, ElevationServiceTests.TestIterations);
        _host.Services.GetRequiredService<ElevationService>().SetPassphrase(hash.Salt, hash.Hash, hash.Iterations);
    }

    private async Task<string> ElevateAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (string)(await response.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
    }

    private Task<HttpResponseMessage> SendWithToken(HttpMethod method, string path, string token, object body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return _client.SendAsync(request);
    }

    private static async Task AssertProblemCodeAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(code, (string?)problem!["code"]);
    }
}
