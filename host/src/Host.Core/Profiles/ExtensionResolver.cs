using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>The input holds no extension id (400).</summary>
public sealed class ExtensionInputException(string message) : Exception(message);

/// <summary>The store has no extension with this id (404).</summary>
public sealed class ExtensionNotFoundException(string message) : Exception(message);

/// <summary>The store could not be reached or answered something unexpected (502).</summary>
public sealed class ExtensionStoreException(string message) : Exception(message);

/// <summary>Turns a pasted store URL or id into an extension with its name and icon.</summary>
public interface IExtensionResolver
{
    Task<ResolvedExtension> ResolveAsync(string input, CancellationToken cancellationToken);
}

/// <summary>
/// Catalog entries resolve without the network. Anything else is looked up in its store: the Chrome Web Store
/// page's og:title and og:image, or the Edge Add-ons product details. Requests go only to the stores' hosts over
/// HTTPS, redirects are followed by hand and checked the same way, and pages and icons are size-capped. The icon
/// comes back as a data: URL. Results are cached for a day.
/// </summary>
public sealed partial class StoreExtensionResolver : IExtensionResolver
{
    public static readonly IReadOnlySet<string> AllowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "chromewebstore.google.com",
        "lh3.googleusercontent.com",
        "microsoftedge.microsoft.com",
        "store-images.s-microsoft.com",
    };

    private const int MaxPageBytes = 2 * 1024 * 1024;
    private const int MaxIconBytes = 64 * 1024;
    private const int MaxRedirects = 3;
    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    [GeneratedRegex("""<meta\s+[^>]*property="og:(?<name>title|image)"[^>]*content="(?<value>[^"]*)"[^>]*>|<meta\s+[^>]*content="(?<value2>[^"]*)"[^>]*property="og:(?<name2>title|image)"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex OpenGraph();

    private readonly HttpClient _http;
    private readonly Catalogs _catalogs;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, ResolvedExtension Extension)> _cache = new(StringComparer.Ordinal);

    public StoreExtensionResolver(HttpClient http, Catalogs catalogs, TimeProvider time)
    {
        _http = http;
        _catalogs = catalogs;
        _time = time;
    }

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        return client;
    }

    public async Task<ResolvedExtension> ResolveAsync(string input, CancellationToken cancellationToken)
    {
        var parsed = ExtensionIdParser.Parse(input)
            ?? throw new ExtensionInputException("Paste a Chrome Web Store or Edge Add-ons link, or an extension id (32 letters a to p).");

        if (Catalogued(parsed) is { } known)
        {
            return known;
        }

        if (_cache.TryGetValue(parsed.ProfileId, out var cached) && _time.GetUtcNow() - cached.At < CacheFor)
        {
            return cached.Extension;
        }

        var resolved = parsed.Store == ExtensionStore.Edge
            ? await ResolveEdgeAsync(parsed, cancellationToken).ConfigureAwait(false)
            : await ResolveChromeAsync(parsed, cancellationToken).ConfigureAwait(false);
        _cache[parsed.ProfileId] = (_time.GetUtcNow(), resolved);
        return resolved;
    }

    private ResolvedExtension? Catalogued(ExtensionRef parsed)
    {
        var entry = _catalogs.ExtensionIndex.GetValueOrDefault(parsed.ProfileId);
        return entry is null ? null : new ResolvedExtension(parsed.Store, parsed.Id, parsed.ProfileId, entry.Name, null, true);
    }

    private async Task<ResolvedExtension> ResolveChromeAsync(ExtensionRef parsed, CancellationToken cancellationToken)
    {
        var page = await GetAsync(new Uri($"https://chromewebstore.google.com/detail/{parsed.Id}"), MaxPageBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new ExtensionNotFoundException($"The Chrome Web Store has no extension {parsed.Id}.");
        var html = Encoding.UTF8.GetString(page.Bytes);

        string? title = null, image = null;
        foreach (Match match in OpenGraph().Matches(html))
        {
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["name2"].Value;
            var value = WebUtility.HtmlDecode(match.Groups["value"].Success ? match.Groups["value"].Value : match.Groups["value2"].Value);
            if (name.Equals("title", StringComparison.OrdinalIgnoreCase))
            {
                title ??= value;
            }
            else
            {
                image ??= value;
            }
        }

        title = title?.Replace(" - Chrome Web Store", "", StringComparison.Ordinal).Trim();
        if (string.IsNullOrEmpty(title) || title.Equals("Chrome Web Store", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtensionNotFoundException($"The Chrome Web Store has no extension {parsed.Id}.");
        }

        return new ResolvedExtension(parsed.Store, parsed.Id, parsed.ProfileId, Shorten(title), await IconAsync(image, cancellationToken).ConfigureAwait(false), false);
    }

    private async Task<ResolvedExtension> ResolveEdgeAsync(ExtensionRef parsed, CancellationToken cancellationToken)
    {
        var details = await GetAsync(new Uri($"https://microsoftedge.microsoft.com/addons/getproductdetailsbycrxid/{parsed.Id}"), MaxPageBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new ExtensionNotFoundException($"The Edge Add-ons store has no extension {parsed.Id}.");
        JsonNode? json;
        try
        {
            json = JsonNode.Parse(details.Bytes);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new ExtensionStoreException("The Edge Add-ons store answered something unexpected.");
        }

        var name = (string?)json?["name"];
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ExtensionNotFoundException($"The Edge Add-ons store has no extension {parsed.Id}.");
        }

        var logo = (string?)json?["logoUrl"];
        if (logo is not null && logo.StartsWith("//", StringComparison.Ordinal))
        {
            logo = "https:" + logo;
        }

        return new ResolvedExtension(parsed.Store, parsed.Id, parsed.ProfileId, Shorten(name), await IconAsync(logo, cancellationToken).ConfigureAwait(false), false);
    }

    /// <summary>The icon as a data: URL, or null when there is none or it is too large or not an image.</summary>
    private async Task<string?> IconAsync(string? url, CancellationToken cancellationToken)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        try
        {
            var icon = await GetAsync(uri, MaxIconBytes, cancellationToken).ConfigureAwait(false);
            return icon is { } found && found.ContentType is "image/png" or "image/jpeg" or "image/webp" or "image/gif"
                ? $"data:{found.ContentType};base64,{Convert.ToBase64String(found.Bytes)}"
                : null;
        }
        catch (ExtensionStoreException)
        {
            // The name is what matters; a missing icon is shown as a placeholder.
            return null;
        }
    }

    /// <returns>Null for 404.</returns>
    private async Task<(byte[] Bytes, string? ContentType)?> GetAsync(Uri uri, int maxBytes, CancellationToken cancellationToken)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !AllowedHosts.Contains(uri.IdnHost))
            {
                throw new ExtensionStoreException($"Refused to contact {uri.IdnHost}: only the extension stores are allowed.");
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                throw new ExtensionStoreException($"The extension store could not be reached: {ex.Message}");
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ExtensionStoreException($"The extension store answered {(int)response.StatusCode}.");
                }

                if (response.Content.Headers.ContentLength > maxBytes)
                {
                    throw new ExtensionStoreException("The extension store's answer was larger than expected.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new ExtensionStoreException("The extension store's answer was larger than expected.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType);
            }
        }

        throw new ExtensionStoreException("The extension store redirected too many times.");
    }

    private static string Shorten(string name) => name.Length > 120 ? name[..120] : name;
}
