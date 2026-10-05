using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.RemoteDesktop;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class HostRemoteDesktopApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public HostRemoteDesktopApiTests()
    {
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
    public async Task Get_ReportsTheHostState()
    {
        _host.RemoteDesktop.State = new RemoteDesktopState("Professional", "Windows 11 Pro", true, 3390, false);

        var state = (await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/remote-desktop"))!;

        Assert.True((bool?)state["supported"]);
        Assert.True((bool?)state["enabled"]);
        Assert.Equal(3390, (int?)state["port"]);
        Assert.False((bool?)state["firewallOpen"]);
        Assert.Equal("Windows 11 Pro", (string?)state["edition"]);
    }

    [Fact]
    public async Task Enable_WithoutElevation_IsRefused()
    {
        var response = await _client.PostAsync("/api/v1/host/remote-desktop/enable", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, _host.RemoteDesktop.AllowCalls);
    }

    [Fact]
    public async Task Enable_AllowsConnections_AndIsAudited()
    {
        var response = await EnableAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.True((bool?)state["enabled"]);
        Assert.True((bool?)state["firewallOpen"]);
        Assert.Equal(1, _host.RemoteDesktop.AllowCalls);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "enableHostRemoteDesktop");
        Assert.Equal("succeeded", (string?)audited["outcome"]);
    }

    [Fact]
    public async Task Enable_OnHomeEdition_Returns409Unsupported()
    {
        _host.RemoteDesktop.State = _host.RemoteDesktop.State with { EditionId = "CoreSingleLanguage", ProductName = "Windows 11 Home Single Language" };

        var response = await EnableAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.RemoteDesktopUnsupported, await CodeAsync(response));
        Assert.Equal(0, _host.RemoteDesktop.AllowCalls);
    }

    [Fact]
    public async Task Enable_OnAnUnelevatedHost_Returns409RequiresInstalledService()
    {
        _host.RemoteDesktop.Elevated = false;

        var response = await EnableAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.RequiresInstalledService, await CodeAsync(response));
        Assert.Equal(0, _host.RemoteDesktop.AllowCalls);
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response) =>
        (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"];

    private async Task<HttpResponseMessage> EnableAsync()
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/host/remote-desktop/enable");
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }
}
