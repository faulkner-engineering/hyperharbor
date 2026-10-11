using System.Text.RegularExpressions;

namespace HyperHarbor.Host.Core.GameReady;

/// <summary>One entry under "Wake Source [n]" in the output of "powercfg /lastwake".</summary>
/// <param name="Type">Device, Fixed Feature, Wake Timer, or whatever Windows printed.</param>
/// <param name="Name">The friendly name, else the description, else the fixed feature type or timer owner.</param>
public sealed record WakeSource(string Type, string? InstancePath, string? Name, string? Description)
{
    public bool IsDevice => string.Equals(Type, "Device", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The latest wake recorded by Windows. Sources is empty when Windows could not tell what woke the PC.</summary>
public sealed record LastWakeResult(int HistoryCount, IReadOnlyList<WakeSource> Sources);

/// <summary>
/// Parses "powercfg /lastwake". The output is English on English Windows and localized elsewhere; a localized
/// output yields no sources, which the test reports as "Windows did not say" rather than as a wrong answer.
/// </summary>
public static partial class LastWakeParser
{
    [GeneratedRegex(@"^\s*Wake History Count\s*-\s*(?<count>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryCount();

    [GeneratedRegex(@"^\s*Wake History \[(?<index>\d+)\]", RegexOptions.IgnoreCase)]
    private static partial Regex HistoryStart();

    [GeneratedRegex(@"^\s*Wake Source \[\d+\]", RegexOptions.IgnoreCase)]
    private static partial Regex SourceStart();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z ]*?)\s*:\s*(?<value>.*?)\s*$")]
    private static partial Regex Field();

    public static LastWakeResult Parse(string? output)
    {
        var sources = new List<WakeSource>();
        var count = 0;
        var inFirstHistory = false;
        var historySeen = false;
        Dictionary<string, string>? current = null;

        void Finish()
        {
            if (current is not null && current.Count > 0)
            {
                sources.Add(ToSource(current));
            }

            current = null;
        }

        foreach (var line in (output ?? string.Empty).Split('\n'))
        {
            if (HistoryCount().Match(line) is { Success: true } countMatch)
            {
                count = int.Parse(countMatch.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }

            if (HistoryStart().IsMatch(line))
            {
                // Only the most recent wake (history entry 0, printed first) is of interest.
                Finish();
                inFirstHistory = !historySeen;
                historySeen = true;
                continue;
            }

            if (!inFirstHistory)
            {
                continue;
            }

            if (SourceStart().IsMatch(line))
            {
                Finish();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (current is not null && Field().Match(line) is { Success: true } field)
            {
                // The first value wins: a field name never repeats inside one source.
                current.TryAdd(field.Groups["key"].Value.Trim(), field.Groups["value"].Value);
            }
        }

        Finish();
        return new LastWakeResult(count, sources);
    }

    private static WakeSource ToSource(Dictionary<string, string> fields)
    {
        string? Get(string key) => fields.TryGetValue(key, out var value) && value.Length > 0 ? value : null;

        var type = Get("Type") ?? "Unknown";
        var name = Get("Friendly Name") ?? Get("Description") ?? Get("Fixed Feature Type") ?? Get("Owner");
        return new WakeSource(type, Get("Instance Path"), name, Get("Description"));
    }
}

public enum WakeAttributionKind
{
    /// <summary>Windows recorded no wake source (or the output could not be read).</summary>
    NoSource,

    Controller,
    OtherDevice,

    /// <summary>A power button, wake timer, or other source that is not a device.</summary>
    OtherSource,
}

/// <param name="Controller">The matching controller or receiver when <paramref name="Kind"/> is Controller.</param>
/// <param name="SourceName">What Windows named as the source.</param>
public sealed record WakeAttribution(WakeAttributionKind Kind, GameController? Controller, string? SourceName);

/// <summary>Decides whether the wake sources of "powercfg /lastwake" include one of the game controllers.</summary>
public static class WakeAttributor
{
    public static WakeAttribution Attribute(LastWakeResult result, IReadOnlyList<GameController> controllers)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controllers);

        if (result.Sources.Count == 0)
        {
            return new WakeAttribution(WakeAttributionKind.NoSource, null, null);
        }

        foreach (var source in result.Sources.Where(source => source.IsDevice))
        {
            if (controllers.FirstOrDefault(controller => Matches(source, controller)) is { } match)
            {
                return new WakeAttribution(WakeAttributionKind.Controller, match, source.Name);
            }
        }

        var first = result.Sources[0];
        return new WakeAttribution(first.IsDevice ? WakeAttributionKind.OtherDevice : WakeAttributionKind.OtherSource, null, first.Name ?? first.Type);
    }

    /// <summary>
    /// The source is the controller when it shares the vendor and product ids with one of the controller's nodes (the
    /// USB device or any of its HID interfaces), or has the same name as one of them.
    /// </summary>
    private static bool Matches(WakeSource source, GameController controller)
    {
        var sourceKey = source.InstancePath is null ? null : ControllerClassifier.VendorProductKey(source.InstancePath);
        foreach (var node in controller.Nodes)
        {
            if (sourceKey is not null && sourceKey == ControllerClassifier.VendorProductKey(node.InstanceId))
            {
                return true;
            }

            if (source.InstancePath is not null && string.Equals(source.InstancePath, node.InstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (source.Name is not null && string.Equals(ControllerWakeEvaluator.BaseName(source.Name), node.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
