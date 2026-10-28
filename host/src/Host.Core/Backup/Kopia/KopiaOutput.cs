using System.Globalization;
using System.Text;
using System.Text.Json;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Backup.Kopia;

public sealed record KopiaSnapshot(
    string Id,
    string SourceHost,
    string SourceUser,
    string SourcePath,
    DateTimeOffset StartTime,
    DateTimeOffset? EndTime,
    long TotalSize,
    string? Description);

/// <summary>Pure parsing of kopia's JSON output and error text. Tolerant of fields it does not know.</summary>
public static class KopiaOutput
{
    public static IReadOnlyList<KopiaSnapshot> Snapshots(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        using var document = ParseDocument(json);
        var root = document.RootElement;
        return root.ValueKind switch
        {
            JsonValueKind.Array => root.EnumerateArray().Select(Snapshot).ToList(),
            JsonValueKind.Object => [Snapshot(root)],
            _ => throw new KopiaException("kopia returned output that is not a snapshot list.", KopiaFailure.Unknown),
        };
    }

    /// <summary>The snapshot that <c>snapshot create --json</c> printed.</summary>
    public static KopiaSnapshot CreatedSnapshot(string json) =>
        Snapshots(json).FirstOrDefault() ?? throw new KopiaException("kopia did not report the snapshot it created.", KopiaFailure.Unknown);

    /// <summary>
    /// The stored size from <c>content stats --raw</c>: "Total Packed" when some content is compressed, otherwise
    /// "Total Bytes". Null when neither line is there.
    /// </summary>
    public static long? StoredBytes(string text)
    {
        long? total = null;
        foreach (var line in text.Split('\n'))
        {
            if (Number(line, "Total Packed:") is { } packed)
            {
                return packed;
            }

            total ??= Number(line, "Total Bytes:");
        }

        return total;

        static long? Number(string line, string label)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(label, StringComparison.Ordinal))
            {
                return null;
            }

            var value = trimmed[label.Length..].TrimStart().Split(' ')[0];
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
        }
    }

    /// <summary>The splitter algorithm in <c>policy show --json</c>, or null when the policy leaves kopia's default.</summary>
    public static string? PolicySplitter(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        using var document = ParseDocument(json);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("splitter", out var splitter)
            ? Text(splitter, "algorithm")
            : null;
    }

    public static KopiaFailure Classify(string errorText)
    {
        var text = errorText.ToLowerInvariant();
        if (text.Contains("invalid repository password") || text.Contains("incorrect password"))
        {
            return KopiaFailure.BadPassphrase;
        }

        if (text.Contains("repository not initialized") || text.Contains("found no repository")
            || text.Contains("unable to open repository") || text.Contains("not a kopia repository")
            || text.Contains("repository format blob"))
        {
            return KopiaFailure.NotARepository;
        }

        if (text.Contains("access denied") || text.Contains("403") || text.Contains("forbidden")
            || text.Contains("invalidaccesskeyid") || text.Contains("signaturedoesnotmatch") || text.Contains("authentication"))
        {
            return KopiaFailure.AccessDenied;
        }

        if (text.Contains("no such host") || text.Contains("connection refused") || text.Contains("dial tcp")
            || text.Contains("i/o timeout") || text.Contains("network is unreachable"))
        {
            return KopiaFailure.Unreachable;
        }

        return KopiaFailure.Unknown;
    }

    /// <summary>Removes the secrets from text that may reach logs or problem details, and shortens it.</summary>
    public static string Sanitize(string text, IEnumerable<string> secrets)
    {
        var clean = text;
        foreach (var secret in secrets.Where(s => s.Length >= 4))
        {
            clean = clean.Replace(secret, "*****", StringComparison.Ordinal);
        }

        clean = new string(clean.Where(c => !char.IsControl(c) || c is '\n' or '\t').ToArray()).Trim();
        return clean.Length > 600 ? clean[..600] + "..." : clean;
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(Utf8Json.WithoutBom(Encoding.UTF8.GetBytes(json)).ToArray());
        }
        catch (JsonException)
        {
            throw new KopiaException("kopia returned output that is not JSON.", KopiaFailure.Unknown);
        }
    }

    private static KopiaSnapshot Snapshot(JsonElement element)
    {
        var source = element.TryGetProperty("source", out var s) ? s : default;
        return new KopiaSnapshot(
            Id: Text(element, "id") ?? throw new KopiaException("A kopia snapshot had no id.", KopiaFailure.Unknown),
            SourceHost: Text(source, "host") ?? string.Empty,
            SourceUser: Text(source, "userName") ?? string.Empty,
            SourcePath: Text(source, "path") ?? string.Empty,
            StartTime: Time(element, "startTime") ?? DateTimeOffset.MinValue,
            EndTime: Time(element, "endTime"),
            TotalSize: Size(element),
            Description: Text(element, "description"));
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time
            : null;

    private static long Size(JsonElement element)
    {
        if (element.TryGetProperty("stats", out var stats) && stats.TryGetProperty("totalSize", out var total) && total.TryGetInt64(out var size))
        {
            return size;
        }

        return element.TryGetProperty("rootEntry", out var root) && root.TryGetProperty("summ", out var summary)
            && summary.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var fallback)
            ? fallback
            : 0;
    }
}
