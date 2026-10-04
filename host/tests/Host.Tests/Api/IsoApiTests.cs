using System.Net;
using System.Net.Http.Headers;
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

public sealed class IsoApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;
    private string? _token;

    public IsoApiTests()
    {
        Directory.CreateDirectory(_host.IsoFolder);
        File.WriteAllBytes(Path.Combine(_host.IsoFolder, "ubuntu.iso"), new byte[10]);
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
    public async Task Upload_StoresTheImageOnTheHost()
    {
        var content = new byte[2 * 1024 * 1024 + 3];
        Random.Shared.NextBytes(content);

        var response = await SendAsync(HttpMethod.Put, "/api/v1/isos/Win%2011%2024H2.iso", Body(content));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Win 11 24H2.iso", (string?)image["name"]);
        Assert.Equal(content.Length, (long?)image["sizeBytes"]);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(_host.IsoFolder, "Win 11 24H2.iso")));
        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "uploadIso");
        Assert.Equal("succeeded", (string?)audited["outcome"]);
        Assert.Equal("name=Win 11 24H2.iso", (string?)audited["detail"]);
    }

    [Fact]
    public async Task Upload_WithoutElevation_IsRefused_AndStoresNothing()
    {
        var response = await _client.PutAsync("/api/v1/isos/new.iso", Body(new byte[10]));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(File.Exists(Path.Combine(_host.IsoFolder, "new.iso")));
    }

    [Fact]
    public async Task Upload_OverAnExistingImage_Returns409()
    {
        var response = await SendAsync(HttpMethod.Put, "/api/v1/isos/ubuntu.iso", Body(new byte[3]));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(10, new FileInfo(Path.Combine(_host.IsoFolder, "ubuntu.iso")).Length);
    }

    [Theory]
    [InlineData("..%5Cescape.iso")]
    [InlineData("image.img")]
    public async Task Upload_WithAnInvalidName_Returns400(string name)
    {
        var response = await SendAsync(HttpMethod.Put, $"/api/v1/isos/{name}", Body(new byte[3]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Rename_ReturnsTheRenamedImage()
    {
        var response = await SendAsync(HttpMethod.Patch, "/api/v1/isos/ubuntu.iso", JsonContent.Create(new { newName = "Ubuntu 24.04.iso" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ubuntu 24.04.iso", (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["name"]);
        Assert.True(File.Exists(Path.Combine(_host.IsoFolder, "Ubuntu 24.04.iso")));
    }

    [Fact]
    public async Task Delete_RemovesTheImage_AndAMissingImageIs404()
    {
        var deleted = await SendAsync(HttpMethod.Delete, "/api/v1/isos/ubuntu.iso");
        var missing = await SendAsync(HttpMethod.Delete, "/api/v1/isos/ubuntu.iso");

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(File.Exists(Path.Combine(_host.IsoFolder, "ubuntu.iso")));
    }

    [Fact]
    public async Task ImageInUse_IsListedWithItsVm_AndCannotBeDeleted()
    {
        _host.Storage.Images.Add(new DiskAttachment(Guid.NewGuid(), "Dev", Path.Combine(_host.IsoFolder, "ubuntu.iso"), false));

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/v1/isos");
        var response = await SendAsync(HttpMethod.Delete, "/api/v1/isos/ubuntu.iso");

        Assert.Equal("Dev", (string?)list![0]!["usedBy"]![0]);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(_host.IsoFolder, "ubuntu.iso")));
    }

    [Fact]
    public async Task FolderChosenInTheTray_IsUsedAtOnce()
    {
        var moved = Path.Combine(_host.DataDirectory, "moved-isos");
        Directory.CreateDirectory(moved);
        File.WriteAllBytes(Path.Combine(moved, "debian.iso"), new byte[1]);

        _host.Services.GetRequiredService<HostSettingsStore>().SetIsoFolder(moved);

        var list = await _client.GetFromJsonAsync<JsonArray>("/api/v1/isos");
        var resources = await _client.GetFromJsonAsync<JsonObject>("/api/v1/host/resources");
        Assert.Equal("debian.iso", (string?)Assert.Single(list!)!["name"]);
        Assert.Equal(moved, (string?)resources!["isoFolder"]);
    }

    private static ByteArrayContent Body(byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return body;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content = null)
    {
        if (_token is null)
        {
            var elevate = await _client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
            _token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
        }

        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(ContractInfo.ElevationHeader, _token);
        return await _client.SendAsync(request);
    }
}
