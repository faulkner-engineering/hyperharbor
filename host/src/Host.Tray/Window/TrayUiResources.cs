namespace HyperHarbor.Host.Tray.Window;

/// <summary>
/// The window's HTML app (webui\dist), embedded in this assembly as HyperHarbor.Tray.Ui/&lt;path&gt; by the project's
/// EmbedTrayUi target. Nothing is written to disk; the window reads each file from here.
/// </summary>
internal static class TrayUiResources
{
    public const string Prefix = "HyperHarbor.Tray.Ui/";

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".svg"] = "image/svg+xml",
        [".png"] = "image/png",
        [".ico"] = "image/x-icon",
        [".woff2"] = "font/woff2",
        [".json"] = "application/json",
    };

    /// <summary>The file at <paramref name="path"/> (relative to the app's root, "" for index.html), or null.</summary>
    public static (Stream Content, string ContentType)? Open(string path)
    {
        var relative = path.TrimStart('/');
        if (relative.Length == 0)
        {
            relative = "index.html";
        }

        // Only plain relative paths: nothing climbs out of the app's root.
        if (relative.Contains("..", StringComparison.Ordinal) || relative.Contains('\\'))
        {
            return null;
        }

        var stream = typeof(TrayUiResources).Assembly.GetManifestResourceStream(Prefix + relative);
        return stream is null
            ? null
            : (stream, ContentTypes.GetValueOrDefault(Path.GetExtension(relative), "application/octet-stream"));
    }

    /// <summary>Every embedded file, for tests.</summary>
    public static IEnumerable<string> Files =>
        typeof(TrayUiResources).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => name[Prefix.Length..]);
}
