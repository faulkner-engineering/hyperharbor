namespace HyperHarbor.Host.Tray.Api;

/// <summary>
/// The API routes the tray window may call (docs/host-vm-management.md): the VM page's list, power actions, compute
/// and disk settings, delete, and the elevation and job routes they need. The page is HTML the tray serves itself, but
/// a request it sends is still checked here, so a fault in the page cannot reach any other route with the tray's
/// device certificate. A route the VM page starts to use is added here with its test.
/// </summary>
public static class TrayApiRoutes
{
    private static readonly (string Method, string[] Segments)[] Allowed =
    [
        ("GET", ["vms"]),
        ("POST", ["vms", "{id}", "actions"]),
        ("GET", ["vms", "{id}", "compute"]),
        ("PATCH", ["vms", "{id}", "compute"]),
        ("GET", ["vms", "{id}", "disks"]),
        ("POST", ["vms", "{id}", "disks", "grow"]),
        ("GET", ["vms", "{id}", "delete-preview"]),
        ("POST", ["vms", "{id}", "delete"]),
        ("GET", ["host", "resources"]),
        ("GET", ["switches"]),
        ("GET", ["auth", "elevation"]),
        ("POST", ["auth", "elevation"]),
        ("GET", ["jobs", "{id}"]),
    ];

    /// <summary>The elevate route, whose token the tray keeps instead of passing it to the page.</summary>
    public const string ElevationPath = "/auth/elevation";

    /// <summary>
    /// True when <paramref name="method"/> and <paramref name="path"/> (relative to /api/v1, starting with "/", no
    /// query) name an allowed route. An ID segment must be a GUID, so a path cannot climb out with "..".
    /// </summary>
    public static bool IsAllowed(string method, string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('?') || path.Contains('#'))
        {
            return false;
        }

        var segments = path[1..].Split('/');
        foreach (var (allowedMethod, pattern) in Allowed)
        {
            if (string.Equals(allowedMethod, method, StringComparison.Ordinal)
                && segments.Length == pattern.Length
                && pattern.Zip(segments).All(pair => pair.First == "{id}"
                    ? Guid.TryParseExact(pair.Second, "D", out _)
                    : string.Equals(pair.First, pair.Second, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}
