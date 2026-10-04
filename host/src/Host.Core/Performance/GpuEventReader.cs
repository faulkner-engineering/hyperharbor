using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>One GPU driver event from the host's System log.</summary>
public sealed record GpuEvent(string Provider, int EventId, DateTimeOffset TimeCreated, string? Message);

/// <summary>Reads GPU driver events from the host's System log.</summary>
public interface IGpuEventSource
{
    IReadOnlyList<GpuEvent> Read(DateTimeOffset since);
}

/// <summary>
/// The System log providers that report GPU driver errors, by vendor, and how events become
/// <see cref="GpuDriverWarning"/>s for the host card.
/// </summary>
public static class GpuDriverEvents
{
    /// <summary>How far back the host card looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>Longest message the API returns; the log's text can be long.</summary>
    public const int MaxMessageLength = 300;

    /// <summary>Display event 4101: the display driver stopped responding and recovered (TDR), for any vendor.</summary>
    public const string DisplayProvider = "Display";

    public const int DisplayTimeoutEventId = 4101;

    public static readonly IReadOnlyDictionary<string, GpuVendor> VendorProviders =
        new Dictionary<string, GpuVendor>(StringComparer.OrdinalIgnoreCase)
        {
            ["nvlddmkm"] = GpuVendor.Nvidia,
            ["amdkmdag"] = GpuVendor.Amd,
            ["amdwddmg"] = GpuVendor.Amd,
            ["igfx"] = GpuVendor.Intel,
            ["igfxn"] = GpuVendor.Intel,
            ["igfxnd"] = GpuVendor.Intel,
        };

    /// <summary>
    /// The System log query: errors and warnings (levels 1 to 3) from the vendor providers, and
    /// Display event 4101, within <paramref name="window"/>.
    /// </summary>
    public static string Query(TimeSpan window)
    {
        var milliseconds = ((long)window.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        var providers = string.Join(" or ", VendorProviders.Keys.Select(name => $"Provider[@Name='{name}']"));
        return $"*[System[((({providers}) and (Level=1 or Level=2 or Level=3))" +
            $" or (Provider[@Name='{DisplayProvider}'] and EventID={DisplayTimeoutEventId}))" +
            $" and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
    }

    /// <summary>
    /// Groups the events from the last <see cref="Window"/> by provider and event ID, newest first.
    /// Each warning carries the most recent event's message.
    /// </summary>
    public static IReadOnlyList<GpuDriverWarning> Summarize(IEnumerable<GpuEvent> events, DateTimeOffset now)
    {
        var since = now - Window;
        return events
            .Where(record => record.TimeCreated >= since && IsGpuEvent(record))
            .GroupBy(record => (Provider: record.Provider.ToLowerInvariant(), record.EventId))
            .Select(group =>
            {
                var latest = group.MaxBy(record => record.TimeCreated)!;
                return new GpuDriverWarning(latest.Provider, latest.EventId, group.Count(), latest.TimeCreated, Describe(latest));
            })
            .OrderByDescending(warning => warning.LastSeen)
            .ToList();
    }

    public static bool IsGpuEvent(GpuEvent record) =>
        VendorProviders.ContainsKey(record.Provider) ||
        (string.Equals(record.Provider, DisplayProvider, StringComparison.OrdinalIgnoreCase) && record.EventId == DisplayTimeoutEventId);

    /// <summary>The event's text on one line, without control characters and capped, or a fallback.</summary>
    public static string Describe(GpuEvent record)
    {
        if (string.IsNullOrWhiteSpace(record.Message))
        {
            return record.EventId == DisplayTimeoutEventId && string.Equals(record.Provider, DisplayProvider, StringComparison.OrdinalIgnoreCase)
                ? "The display driver stopped responding and has recovered."
                : $"The {record.Provider} driver reported event {record.EventId.ToString(CultureInfo.InvariantCulture)}.";
        }

        var text = new StringBuilder(record.Message.Length);
        var space = false;
        foreach (var c in record.Message.Trim())
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = true;
                continue;
            }

            if (space && text.Length > 0)
            {
                text.Append(' ');
            }

            space = false;
            text.Append(c);
        }

        return text.Length <= MaxMessageLength ? text.ToString() : text.ToString(0, MaxMessageLength - 1) + "…";
    }
}

/// <summary>
/// The System log through EventLogReader. A member of Hyper-V Administrators can read it without
/// administrator rights. Read failures are logged and give no events, so the host card never fails
/// because of the log.
/// </summary>
public sealed class EventLogGpuEventSource(ILogger<EventLogGpuEventSource> logger) : IGpuEventSource
{
    /// <summary>Most events read per query; a driver in a crash loop can log thousands.</summary>
    private const int MaxEvents = 1000;

    public IReadOnlyList<GpuEvent> Read(DateTimeOffset since)
    {
        var window = DateTimeOffset.UtcNow - since;
        var events = new List<GpuEvent>();
        try
        {
            var query = new EventLogQuery("System", PathType.LogName, GpuDriverEvents.Query(window)) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            for (var record = reader.ReadEvent(); record is not null && events.Count < MaxEvents; record = reader.ReadEvent())
            {
                using (record)
                {
                    events.Add(new GpuEvent(
                        record.ProviderName,
                        record.Id,
                        record.TimeCreated is { } created ? new DateTimeOffset(created.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.UtcNow,
                        TryFormat(record)));
                }
            }
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read GPU driver events from the System log.");
        }

        return events;
    }

    /// <summary>The message needs the provider's resources, which may be missing (for example after a driver was removed).</summary>
    private static string? TryFormat(EventRecord record)
    {
        try
        {
            return record.FormatDescription();
        }
        catch (EventLogException)
        {
            return null;
        }
    }
}

/// <summary>The host card's driver warnings, read from the log at most once every five minutes.</summary>
public sealed class GpuEventReader(IGpuEventSource source, TimeProvider time)
{
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private IReadOnlyList<GpuDriverWarning>? _cached;
    private DateTimeOffset _readAt;

    public IReadOnlyList<GpuDriverWarning> Warnings()
    {
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (_cached is null || now - _readAt >= CacheLifetime)
            {
                _cached = GpuDriverEvents.Summarize(source.Read(now - GpuDriverEvents.Window), now);
                _readAt = now;
            }

            return _cached;
        }
    }
}
