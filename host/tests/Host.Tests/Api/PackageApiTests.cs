using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Tests.Api;

public sealed class PackageApiTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly X509Certificate2 _certificate = TestHost.CreateClientCertificate();
    private readonly HttpClient _client;

    public PackageApiTests()
    {
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
    public async Task Search_ReturnsWingetPackages_WithACountWithinLimits()
    {
        var found = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/packages/search?q=%20git%20&count=500"))!;

        Assert.Equal("Git.Git", (string?)found[0]!["id"]);
        Assert.Equal(("git", 50), Assert.Single(_host.PackageSearch.Searches));
    }

    [Theory]
    [InlineData("/api/v1/packages/search")]
    [InlineData("/api/v1/packages/search?q=%20")]
    [InlineData("/api/v1/packages/search?q=a%0Ab")]
    public async Task AMissingOrOddQuery_Is400(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_host.PackageSearch.Searches);
    }

    [Fact]
    public async Task WithoutPowerShell7_SearchIs409WingetUnavailable()
    {
        _host.PackageSearch.Available = false;

        var response = await _client.GetAsync("/api/v1/packages/search?q=git");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ContractInfo.ProblemCodes.WingetUnavailable, (string?)(await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]);
    }

    [Fact]
    public async Task TheCatalog_ListsAliases_AndThePopularOnes()
    {
        var catalog = (await _client.GetFromJsonAsync<JsonArray>("/api/v1/packages/catalog"))!;

        var vscode = catalog.Single(item => (string?)item!["alias"] == "vscode")!;
        Assert.Equal(("Microsoft.VisualStudioCode", true), ((string?)vscode["id"], (bool)vscode["popular"]!));
        Assert.Equal(Catalogs.Default.Packages.Count, catalog.Count);
    }
}
