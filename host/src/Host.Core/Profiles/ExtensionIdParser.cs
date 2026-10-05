using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.Profiles;

public enum ExtensionStore
{
    Chrome,
    Edge,
}

/// <summary>A browser extension by store and id. Profiles write Edge ids as edge:&lt;id&gt;.</summary>
public sealed record ExtensionRef(ExtensionStore Store, string Id)
{
    public const string EdgePrefix = "edge:";

    /// <summary>The form used in profiles.</summary>
    public string ProfileId => Store == ExtensionStore.Edge ? EdgePrefix + Id : Id;
}

/// <summary>
/// Reads an extension id from what people paste: a Chrome Web Store or Edge Add-ons URL, a profile entry
/// (edge:&lt;id&gt;), or a bare id. Extension ids are 32 letters from a to p.
/// </summary>
public static partial class ExtensionIdParser
{
    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex Id();

    /// <returns>Null when the input holds no extension id.</returns>
    public static ExtensionRef? Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith(ExtensionRef.EdgePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var edgeId = text[ExtensionRef.EdgePrefix.Length..].ToLowerInvariant();
            return Id().IsMatch(edgeId) ? new ExtensionRef(ExtensionStore.Edge, edgeId) : null;
        }

        if (Id().IsMatch(text.ToLowerInvariant()))
        {
            return new ExtensionRef(ExtensionStore.Chrome, text.ToLowerInvariant());
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var store = uri.IdnHost.ToLowerInvariant() switch
        {
            "chromewebstore.google.com" or "chrome.google.com" => ExtensionStore.Chrome,
            "microsoftedge.microsoft.com" => ExtensionStore.Edge,
            _ => (ExtensionStore?)null,
        };
        if (store is null)
        {
            return null;
        }

        // Store URLs end with the id: /detail/<slug>/<id>, /detail/<id>, /webstore/detail/<slug>/<id>,
        // /addons/detail/<slug>/<id>. The last segment that is an id wins; query and fragment are ignored.
        var id = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => Uri.UnescapeDataString(segment).ToLowerInvariant())
            .LastOrDefault(segment => Id().IsMatch(segment));
        return id is null ? null : new ExtensionRef(store.Value, id);
    }
}
