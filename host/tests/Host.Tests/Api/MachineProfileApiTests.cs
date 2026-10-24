using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Host.Tests.Elevation;
using HyperHarbor.Host.Tests.Profiles;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace HyperHarbor.Host.Tests.Api;

public sealed class MachineProfileApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string Path = "/api/v1/machine-profiles";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;
    private string? _token;

    public MachineProfileApiTests()
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

    private Guid UserId => _host.Services.GetRequiredService<Core.Users.UserStore>().GetOrCreateDefault().UserId;

    [Fact]
    public async Task AProfile_IsSavedAsYaml_ListedReadAndExported()
    {
        var created = await SendAsync(HttpMethod.Post, Path, JsonContent.Create(MachineProfileTests.Sample(), options: ContractJson.Options));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal($"{Path}/gaming-rig", created.Headers.Location?.OriginalString);

        var summary = Assert.Single((await _client.GetFromJsonAsync<JsonArray>(Path))!)!;
        Assert.Equal(("Gaming rig", "windows", 8, 16384L, true, true, true, 4), (
            (string?)summary["name"], (string?)summary["os"], (int)summary["processors"]!, (long)summary["memoryMb"]!,
            (bool)summary["gpu"]!, (bool)summary["tpm"]!, (bool)summary["backup"]!, (int)summary["softwareCount"]!));

        var stored = (await _client.GetFromJsonAsync<JsonObject>($"{Path}/gaming-rig"))!;
        Assert.Equal("fullScreen", (string?)stored["profile"]!["display"]!["connection"]!["window"]);
        Assert.Equal("discord", (string?)stored["profile"]!["install"]![1]!["id"]);

        var yaml = await _client.GetAsync($"{Path}/gaming-rig/yaml");
        Assert.Equal("application/yaml", yaml.Content.Headers.ContentType?.MediaType);
        Assert.Contains("schemaVersion: 1", await yaml.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(File.Exists(System.IO.Path.Combine(_host.DataDirectory, MachineProfileStore.FolderName, UserId.ToString("D"), "gaming-rig.yaml")));

        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "createMachineProfile");
        Assert.Equal("name=Gaming rig, os=Windows, gpu=True, backup=True, install=2", (string?)audited["detail"]);
    }

    [Fact]
    public async Task Saving_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync(Path, MachineProfileTests.Sample(), ContractJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnInvalidProfile_Is400_WithFieldPaths()
    {
        var response = await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "Bad", os = "windows", compute = new { processors = 0 } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray();
        Assert.Equal("compute.processors", (string?)Assert.Single(errors)!["field"]);
    }

    [Fact]
    public async Task Import_SavesAYamlFile_AndRefusesSomethingElse()
    {
        const string yaml = "schemaVersion: 1\nname: Imported\nos: windows\nfirmware:\n  tpm: true\ninstall:\n  - git   # Git\n";
        var imported = await SendAsync(HttpMethod.Post, $"{Path}/import", new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        var setupProfile = await SendAsync(HttpMethod.Post, $"{Path}/import", new StringContent("schemaVersion: 1\nname: Old\n", Encoding.UTF8, "application/yaml"));

        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        Assert.Equal("imported", (string?)(await imported.Content.ReadFromJsonAsync<JsonObject>())!["id"]);
        Assert.Equal(HttpStatusCode.BadRequest, setupProfile.StatusCode);
        Assert.Equal("yaml", (string?)(await setupProfile.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task UpdateAndDelete_WorkOnTheCallersProfiles_AndMissingOnesAre404()
    {
        await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "First", os = "windows" }));
        var updated = await SendAsync(HttpMethod.Put, $"{Path}/first", JsonContent.Create(new { name = "First, renamed", os = "windows", network = new { @switch = "LAN" } }));
        var missing = await SendAsync(HttpMethod.Put, $"{Path}/nope", JsonContent.Create(new { name = "x", os = "windows" }));
        var traversal = await _client.GetAsync($"{Path}/..%5Cusers");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("LAN", (string?)(await updated.Content.ReadFromJsonAsync<JsonObject>())!["profile"]!["network"]!["switch"]);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("Machine profile not found", (string?)(await missing.Content.ReadFromJsonAsync<JsonObject>())!["title"]);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"{Path}/first")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>(Path))!);
    }

    [Fact]
    public async Task AHandEditedFileThatNoLongerReads_IsListedWithItsProblem()
    {
        await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "Broken", os = "windows" }));
        var file = System.IO.Path.Combine(_host.DataDirectory, MachineProfileStore.FolderName, UserId.ToString("D"), "broken.yaml");
        await File.WriteAllTextAsync(file, "schemaVersion: 1\nname: Broken\nos: windows\ncompute:\n  processors: 0\n");

        var summary = Assert.Single((await _client.GetFromJsonAsync<JsonArray>(Path))!)!;
        var get = await _client.GetAsync($"{Path}/broken");

        Assert.Contains("compute.processors", (string?)summary["error"], StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }

    [Fact]
    public async Task TheSchema_IsServed()
    {
        var response = await _client.GetAsync($"{Path}/schema");

        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Catalogs.MachineProfileSchema, await response.Content.ReadAsStringAsync());
        Assert.NotNull(JsonNode.Parse(Catalogs.MachineProfileSchema));
    }

    [Fact]
    public async Task Templates_AreServed_AndAreNotInTheUsersProfiles()
    {
        var templates = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/machine-profile-templates"))!;

        Assert.Equal(["windows-workstation", "windows-burner", "ubuntu-dev-server"], templates.Select(template => (string?)template!["id"]));
        Assert.Equal("linux", (string?)templates[2]!["profile"]!["os"]);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>(Path))!);
        var missing = await SendAsync(HttpMethod.Post, "/api/v1/machine-profile-templates/nothing/vms", JsonContent.Create(new { name = "Rig", isoName = "win11.iso" }));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WallpaperImages_AreUploadedListedAndDeleted_WithElevation()
    {
        const string images = "/api/v1/profile-images";
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

        var unelevated = await _client.PutAsync($"{images}/harbor.png", new ByteArrayContent(png));
        var uploaded = await SendAsync(HttpMethod.Put, $"{images}/harbor%20night.png", new ByteArrayContent(png));
        var notAnImage = await SendAsync(HttpMethod.Put, $"{images}/notes.png", new ByteArrayContent("hello"u8.ToArray()));
        var badName = await SendAsync(HttpMethod.Put, $"{images}/script.ps1", new ByteArrayContent(png));

        Assert.Equal(HttpStatusCode.Forbidden, unelevated.StatusCode);
        Assert.Equal(HttpStatusCode.OK, uploaded.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAnImage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        var listed = Assert.Single((await _client.GetFromJsonAsync<JsonArray>(images))!)!;
        Assert.Equal(("harbor night.png", 11L), ((string?)listed["name"], (long)listed["sizeBytes"]!));
        var audited = _host.AuditEntries().Single(entry => (string?)entry["action"] == "uploadProfileImage" && (string?)entry["outcome"] == "succeeded");
        Assert.Equal("name=harbor night.png", (string?)audited["detail"]);
        Assert.Equal(png, await _client.GetByteArrayAsync($"{images}/harbor%20night.png/content"));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{images}/other.png/content")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"{images}/..%5Cother.png/content")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Delete, $"{images}/harbor%20night.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(HttpMethod.Delete, $"{images}/harbor%20night.png")).StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>(images))!);
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
