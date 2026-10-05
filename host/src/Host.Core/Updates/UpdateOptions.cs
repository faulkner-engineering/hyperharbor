namespace HyperHarbor.Host.Core.Updates;

/// <summary>Update settings, bound from the "Update" configuration section.</summary>
public sealed class UpdateOptions
{
    public const string SectionName = "Update";
    public const string StableChannel = "stable";

    /// <summary>The channel to follow; the host settings (tray) override it.</summary>
    public string Channel { get; set; } = StableChannel;

    public UpdateMode Mode { get; set; } = UpdateMode.Auto;

    public double CheckIntervalHours { get; set; } = 6;

    /// <summary>How long nothing may be in progress before an automatic install.</summary>
    public double IdleMinutes { get; set; } = 10;

    /// <summary>Host local time (HH:mm) when automatic installs may start without the idle wait; empty for none.</summary>
    public string? MaintenanceTime { get; set; } = "03:00";

    public double MaintenanceWindowMinutes { get; set; } = 60;

    public TimeOnly? ParsedMaintenanceTime =>
        TimeOnly.TryParseExact(MaintenanceTime, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : null;

    /// <summary>Manifest URL for each channel.</summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        // GitHub's "latest" release excludes prereleases, so it serves the stable channel.
        [StableChannel] = "https://github.com/faulkner-engineering/hyperharbor/releases/latest/download/latest.json",
        ["beta"] = "https://github.com/faulkner-engineering/hyperharbor/releases/download/channel-beta/latest.json",
    };

    /// <summary>
    /// Hosts the manifest and the package may come from, including every redirect on the way. GitHub release
    /// assets redirect from github.com to its asset hosts.
    /// </summary>
    public List<string> AllowedHosts { get; set; } =
        ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Largest package accepted, in bytes; the manifest's size must not exceed it.</summary>
    public long MaxPackageBytes { get; set; } = 1024L * 1024 * 1024;

    /// <summary>The manifest URL for <paramref name="channel"/>.</summary>
    /// <exception cref="ArgumentException">The channel is not configured.</exception>
    public Uri ManifestUrl(string channel) =>
        Channels.TryGetValue(channel, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri
            : throw new ArgumentException($"The update channel \"{channel}\" is not configured.", nameof(channel));
}
