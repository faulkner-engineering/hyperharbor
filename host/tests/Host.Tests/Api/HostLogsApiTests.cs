using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class HostLogsApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public HostLogsApiTests()
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
    public async Task Bundle_WithoutElevation_IsRefused()
    {
        var response = await _client.PostAsync("/api/v1/host/logs/bundle", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Bundle_ReturnsAZipWithTheLogsAndASummary_AndIsAudited()
    {
        Directory.CreateDirectory(Path.Combine(_host.DataDirectory, "logs"));
        File.WriteAllText(Path.Combine(_host.DataDirectory, "logs", "host-20261006.log"), "an entry");

        var response = await BundleAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("hyperharbor-logs-", response.Content.Headers.ContentDisposition?.FileName);
        using var archive = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.NotNull(archive.GetEntry("logs/host-20261006.log"));
        using var summary = new StreamReader(archive.GetEntry("summary.txt")!.Open());
        var text = await summary.ReadToEndAsync();
        Assert.Contains($"API version: {ContractInfo.ApiVersion}", text);
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "downloadHostLogs");
        Assert.Equal("succeeded", (string?)audited["outcome"]);
    }

    private async Task<HttpResponseMessage> BundleAsync()
    {
        var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
        var token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/host/logs/bundle");
        request.Headers.Add(ContractInfo.ElevationHeader, token);
        return await _client.SendAsync(request);
    }
}
