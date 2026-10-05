using System.Net;
using System.Text;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class ExtensionResolverTests
{
    private const string GoogleDocsOffline = "ghbmnnjooekpmoecnnnilnnbdlolhkhi";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly FakeStore _store = new();

    /// <summary>Catalogs with no extensions, so every lookup goes to the (fake) store.</summary>
    internal static Catalogs EmptyExtensionCatalog() => new(file => file == "extensions.yaml" ? "[]" : ReadRepositoryCatalog(file));

    private static string ReadRepositoryCatalog(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "catalogs")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "catalogs", file));
    }

    private StoreExtensionResolver Resolver(Catalogs? catalogs = null) =>
        new(new HttpClient(_store), catalogs ?? EmptyExtensionCatalog(), TimeProvider.System);

    [Fact]
    public async Task ACatalogEntry_ResolvesWithoutTheNetwork()
    {
        var chrome = await Resolver(Catalogs.Default).ResolveAsync("https://chromewebstore.google.com/detail/dark-reader/eimadpbcbfnmbkopoojfekhnkhdbieeh", CancellationToken.None);
        var edgeOnly = await Resolver(Catalogs.Default).ResolveAsync("https://microsoftedge.microsoft.com/addons/detail/ublock-origin/odfafepnkmbhccpbejgmiehpchacaeak", CancellationToken.None);
        var edgeOfChromeEntry = await Resolver(Catalogs.Default).ResolveAsync("edge:jbkfoedolllekgbhcbcoahefnbanhhlh", CancellationToken.None);

        Assert.Equal(new ResolvedExtension(ExtensionStore.Chrome, "eimadpbcbfnmbkopoojfekhnkhdbieeh", "eimadpbcbfnmbkopoojfekhnkhdbieeh", "Dark Reader", null, true), chrome);
        Assert.Equal(("edge:odfafepnkmbhccpbejgmiehpchacaeak", "uBlock Origin", true), (edgeOnly.ProfileId, edgeOnly.Name, edgeOnly.InCatalog));
        Assert.Equal("Bitwarden Password Manager", edgeOfChromeEntry.Name);
        Assert.Empty(_store.Requested);
    }

    [Fact]
    public async Task AChromeWebStoreLink_IsLookedUp_FollowingTheStoresRedirect()
    {
        _store.Routes[$"https://chromewebstore.google.com/detail/{GoogleDocsOffline}"] = () => Redirect($"/detail/google-docs-offline/{GoogleDocsOffline}");
        _store.Routes[$"https://chromewebstore.google.com/detail/google-docs-offline/{GoogleDocsOffline}"] = () => Html(
            """<html><head><meta property="og:title" content="Google Docs Offline - Chrome Web Store"><meta property="og:image" content="https://lh3.googleusercontent.com/icon=s128"></head></html>""");
        _store.Routes["https://lh3.googleusercontent.com/icon=s128"] = () => Image(Png, "image/png");

        var resolver = Resolver();
        var resolved = await resolver.ResolveAsync(GoogleDocsOffline, CancellationToken.None);

        Assert.Equal("Google Docs Offline", resolved.Name);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(Png), resolved.IconDataUrl);
        Assert.False(resolved.InCatalog);

        // A second lookup comes from the cache.
        var requests = _store.Requested.Count;
        Assert.Equal(resolved, await resolver.ResolveAsync($"https://chromewebstore.google.com/detail/x/{GoogleDocsOffline}", CancellationToken.None));
        Assert.Equal(requests, _store.Requested.Count);
    }

    [Fact]
    public async Task AnEdgeAddonsLink_UsesTheProductDetails()
    {
        const string edgeId = "odfafepnkmbhccpbejgmiehpchacaeak";
        _store.Routes[$"https://microsoftedge.microsoft.com/addons/getproductdetailsbycrxid/{edgeId}"] = () => Json("""{"name":"uBlock Origin","logoUrl":"//store-images.s-microsoft.com/image/logo"}""");
        _store.Routes["https://store-images.s-microsoft.com/image/logo"] = () => Image(Png, "image/png");

        var resolved = await Resolver().ResolveAsync($"https://microsoftedge.microsoft.com/addons/detail/ublock-origin/{edgeId}", CancellationToken.None);

        Assert.Equal((ExtensionStore.Edge, "edge:" + edgeId, "uBlock Origin"), (resolved.Store, resolved.ProfileId, resolved.Name));
        Assert.NotNull(resolved.IconDataUrl);
    }

    [Fact]
    public async Task AnUnknownId_IsNotFound_AndJunkInputIsRefused()
    {
        _store.Routes[$"https://chromewebstore.google.com/detail/{GoogleDocsOffline}"] = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        await Assert.ThrowsAsync<ExtensionNotFoundException>(() => Resolver().ResolveAsync(GoogleDocsOffline, CancellationToken.None));
        await Assert.ThrowsAsync<ExtensionInputException>(() => Resolver().ResolveAsync("not an extension", CancellationToken.None));
    }

    [Fact]
    public async Task ARedirectAwayFromTheStores_IsRefused()
    {
        _store.Routes[$"https://chromewebstore.google.com/detail/{GoogleDocsOffline}"] = () => Redirect("https://example.com/detail");

        var error = await Assert.ThrowsAsync<ExtensionStoreException>(() => Resolver().ResolveAsync(GoogleDocsOffline, CancellationToken.None));

        Assert.Contains("example.com", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_store.Requested, url => url.Contains("example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnIconThatIsNotAnImage_OrTooLarge_IsLeftOut()
    {
        _store.Routes[$"https://chromewebstore.google.com/detail/{GoogleDocsOffline}"] = () => Html(
            """<meta content="Docs - Chrome Web Store" property="og:title"><meta property="og:image" content="https://lh3.googleusercontent.com/big">""");
        _store.Routes["https://lh3.googleusercontent.com/big"] = () => Image(new byte[200 * 1024], "image/png");

        var resolved = await Resolver().ResolveAsync(GoogleDocsOffline, CancellationToken.None);

        Assert.Equal("Docs", resolved.Name);
        Assert.Null(resolved.IconDataUrl);
    }

    /// <summary>Set HH_EXTENSION_LIVE=1 to look up real store entries.</summary>
    [EnvironmentFact("HH_EXTENSION_LIVE")]
    public async Task Live_BothStores_ResolveNamesAndIcons()
    {
        var resolver = new StoreExtensionResolver(StoreExtensionResolver.CreateHttpClient("HyperHarbor-Host-Tests"), EmptyExtensionCatalog(), TimeProvider.System);

        var chrome = await resolver.ResolveAsync($"https://chromewebstore.google.com/detail/{GoogleDocsOffline}", CancellationToken.None);
        var edge = await resolver.ResolveAsync("https://microsoftedge.microsoft.com/addons/detail/ublock-origin/odfafepnkmbhccpbejgmiehpchacaeak", CancellationToken.None);

        Assert.Contains("Docs", chrome.Name, StringComparison.Ordinal);
        Assert.StartsWith("data:image/", chrome.IconDataUrl, StringComparison.Ordinal);
        Assert.Contains("uBlock", edge.Name, StringComparison.Ordinal);
        Assert.StartsWith("data:image/", edge.IconDataUrl, StringComparison.Ordinal);
    }

    /// <summary>Set HH_EXTENSION_LIVE=1 to check every catalog id against its store.</summary>
    [EnvironmentFact("HH_EXTENSION_LIVE")]
    public async Task Live_EveryCatalogId_IsTheExtensionItSaysItIs()
    {
        var resolver = new StoreExtensionResolver(StoreExtensionResolver.CreateHttpClient("HyperHarbor-Host-Tests"), EmptyExtensionCatalog(), TimeProvider.System);
        var problems = new List<string>();
        foreach (var entry in Catalogs.Default.Extensions)
        {
            foreach (var input in entry.EdgeId is null ? [entry.ProfileId] : new[] { entry.ProfileId, "edge:" + entry.EdgeId })
            {
                try
                {
                    var resolved = await resolver.ResolveAsync(input, CancellationToken.None);
                    var word = entry.Name.Split(' ')[0];
                    if (!resolved.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add($"{input}: catalog says {entry.Name}, store says {resolved.Name}");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"{input} ({entry.Name}): {ex.Message}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image(byte[] bytes, string type)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private sealed class FakeStore : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpResponseMessage>> Routes { get; } = [];

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            return Task.FromResult(Routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
