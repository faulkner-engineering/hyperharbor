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

public sealed class SetupProfileApiTests : IDisposable
{
    private const string Passphrase = "correct horse battery";
    private const string Path = "/api/v1/setup-profiles";

    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;
    private string? _token;

    public SetupProfileApiTests()
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
    public async Task AProfile_IsSavedAsYaml_ListedReadAndExported()
    {
        var created = await SendAsync(HttpMethod.Post, Path, JsonContent.Create(ProfileYamlTests.Sample(), options: ContractJson.Options));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var saved = (await created.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("dev-workstation", (string?)saved["id"]);
        Assert.Equal($"{Path}/dev-workstation", created.Headers.Location?.OriginalString);

        var list = (await _client.GetFromJsonAsync<JsonArray>(Path))!;
        var summary = Assert.Single(list)!;
        Assert.Equal(("Dev workstation", 4, 4, 3, 2, "Brave"), ((string?)summary["name"], (int)summary["installCount"]!, (int)summary["removeCount"]!, (int)summary["tweakCount"]!, (int)summary["extensionCount"]!, (string?)summary["browser"]));

        var stored = (await _client.GetFromJsonAsync<JsonObject>($"{Path}/dev-workstation"))!;
        Assert.Equal("Visual Studio Code", (string?)stored["profile"]!["install"]![1]!["name"]);

        var yaml = await _client.GetAsync($"{Path}/dev-workstation/yaml");
        Assert.Equal("application/yaml", yaml.Content.Headers.ContentType?.MediaType);
        var text = await yaml.Content.ReadAsStringAsync();
        Assert.StartsWith("# yaml-language-server: $schema=", text, StringComparison.Ordinal);
        Assert.Contains("# Visual Studio Code", text, StringComparison.Ordinal);
        Assert.True(File.Exists(System.IO.Path.Combine(_host.DataDirectory, "profiles", UserId.ToString("D"), "dev-workstation.yaml")));

        var audited = _host.AuditEntries().Last(entry => (string?)entry["action"] == "createSetupProfile");
        Assert.Equal("name=Dev workstation, install=4, tweaks=3", (string?)audited["detail"]);
    }

    [Fact]
    public async Task Saving_NeedsElevation()
    {
        var response = await _client.PostAsJsonAsync(Path, ProfileYamlTests.Sample(), ContractJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnInvalidProfile_Is400_WithFieldPaths()
    {
        var response = await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "Bad", install = new[] { new { id = "not valid" } } }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsArray();
        Assert.Equal("install[0]", (string?)Assert.Single(errors)!["field"]);
    }

    [Fact]
    public async Task Import_SavesAYamlFile_AndRefusesSomethingElse()
    {
        const string yaml = "schemaVersion: 1\nname: Imported\ninstall:\n  - git   # Git\n";
        var imported = await SendAsync(HttpMethod.Post, $"{Path}/import", new StringContent(yaml, Encoding.UTF8, "application/yaml"));
        var refused = await SendAsync(HttpMethod.Post, $"{Path}/import", new StringContent("just: [text", Encoding.UTF8, "application/yaml"));

        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        Assert.Equal("imported", (string?)(await imported.Content.ReadFromJsonAsync<JsonObject>())!["id"]);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("yaml", (string?)(await refused.Content.ReadFromJsonAsync<JsonObject>())!["errors"]![0]!["field"]);
    }

    [Fact]
    public async Task UpdateAndDelete_WorkOnTheCallersProfiles_AndMissingOnesAre404()
    {
        await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "First" }));
        var updated = await SendAsync(HttpMethod.Put, $"{Path}/first", JsonContent.Create(new { name = "First, renamed", install = new[] { new { id = "vscode" } } }));
        var missing = await SendAsync(HttpMethod.Put, $"{Path}/nope", JsonContent.Create(new { name = "x" }));
        var traversal = await _client.GetAsync($"{Path}/..%5Cusers");

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("first", (string?)(await updated.Content.ReadFromJsonAsync<JsonObject>())!["id"]);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, traversal.StatusCode);

        var deleted = await SendAsync(HttpMethod.Delete, $"{Path}/first");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await _client.GetFromJsonAsync<JsonArray>(Path))!);
    }

    [Fact]
    public async Task AHandEditedFileThatNoLongerReads_IsListedWithItsProblem()
    {
        await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "Broken" }));
        var file = System.IO.Path.Combine(_host.DataDirectory, "profiles", UserId.ToString("D"), "broken.yaml");
        await File.WriteAllTextAsync(file, "schemaVersion: 1\nname: Broken\ninstall: [oops\n");

        var summary = Assert.Single((await _client.GetFromJsonAsync<JsonArray>(Path))!)!;
        var get = await _client.GetAsync($"{Path}/broken");

        Assert.NotNull((string?)summary["error"]);
        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
    }

    [Fact]
    public async Task TheCatalog_IsServed_AndProfilesNeverTakeItsName()
    {
        var catalog = (await _client.GetFromJsonAsync<JsonObject>($"{Path}/catalog"))!;
        var created = await SendAsync(HttpMethod.Post, Path, JsonContent.Create(new { name = "Catalog" }));

        Assert.Contains(catalog["tweaks"]!.AsArray(), tweak => (string?)tweak!["id"] == "explorer.showFileExtensions");
        Assert.Equal(["chrome", "edge", "brave"], catalog["browsers"]!.AsArray().Select(browser => (string)browser!["id"]!));
        Assert.Contains(catalog["policies"]!.AsArray(), policy => (string?)policy!["key"] == "restoreOnStartup" && policy["values"]!["5"] is not null);
        Assert.Equal("catalog-2", (string?)(await created.Content.ReadFromJsonAsync<JsonObject>())!["id"]);
    }

    [Fact]
    public async Task TheSchema_IsServed()
    {
        var response = await _client.GetAsync($"{Path}/schema");

        Assert.Equal("application/schema+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Catalogs.ProfileSchema, await response.Content.ReadAsStringAsync());
    }

    private Guid UserId => _host.Services.GetRequiredService<Core.Users.UserStore>().GetOrCreateDefault().UserId;

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
