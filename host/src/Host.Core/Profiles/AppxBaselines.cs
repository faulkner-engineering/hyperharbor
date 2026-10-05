using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>Where a baseline came from.</summary>
public sealed record AppxBaselineOrigin(AppxBaselineSource How, Guid? VmId, string? VmName);

/// <summary>The provisioned packages of a clean Windows install of one build and edition.</summary>
/// <param name="Key">Major build and edition, for example 10.0.26100/Professional.</param>
/// <param name="Build">The full build when recorded, for example 10.0.26100.2033.</param>
public sealed record AppxBaseline(
    string Key,
    string Build,
    string Edition,
    DateTimeOffset RecordedAt,
    AppxBaselineOrigin Source,
    IReadOnlyList<string> Packages)
{
    /// <summary>"10.0.26100/Professional" from the major build number and the edition.</summary>
    public static string KeyOf(string currentBuild, string edition) => $"10.0.{currentBuild}/{edition}";
}

/// <summary>Which packages were removed from, added to, or kept from a baseline. Names only; versions do not matter.</summary>
public sealed record AppxDiff(IReadOnlyList<string> Removed, IReadOnlyList<string> Added, IReadOnlyList<string> Kept)
{
    /// <summary>Compares names case-insensitively; each list keeps the casing of the side it comes from, sorted.</summary>
    public static AppxDiff Compare(IEnumerable<string> baseline, IEnumerable<string> current)
    {
        var before = new HashSet<string>(baseline, StringComparer.OrdinalIgnoreCase);
        var now = new HashSet<string>(current, StringComparer.OrdinalIgnoreCase);
        return new AppxDiff(
            Sorted(before.Where(name => !now.Contains(name))),
            Sorted(now.Where(name => !before.Contains(name))),
            Sorted(now.Where(before.Contains)));
    }

    private static List<string> Sorted(IEnumerable<string> names) => names.Order(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>
/// Clean Appx baselines for every Windows build and edition seen, in &lt;data&gt;\appx-baselines.json (ProtectedFile
/// ACL). Host-wide, because they describe Windows images rather than anyone's choices.
/// </summary>
public sealed class AppxBaselineStore
{
    public const string FileName = "appx-baselines.json";
    private const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(ContractJson.Options) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<AppxBaseline>? _baselines;

    public AppxBaselineStore(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, FileName);
    }

    public IReadOnlyList<AppxBaseline> List()
    {
        lock (_gate)
        {
            return [.. Load()];
        }
    }

    /// <summary>The baseline for this build and edition, or else one for the same build and another edition (approximate).</summary>
    public (AppxBaseline Baseline, bool Approximate)? Find(string currentBuild, string edition)
    {
        lock (_gate)
        {
            var baselines = Load();
            var key = AppxBaseline.KeyOf(currentBuild, edition);
            if (baselines.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase)) is { } exact)
            {
                return (exact, false);
            }

            var prefix = AppxBaseline.KeyOf(currentBuild, "");
            return baselines.FirstOrDefault(item => item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } sameBuild
                ? (sameBuild, true)
                : null;
        }
    }

    /// <summary>Saves the baseline, replacing one with the same key unless <paramref name="onlyIfMissing"/>.</summary>
    /// <returns>False when <paramref name="onlyIfMissing"/> and a baseline for the key exists.</returns>
    public bool Save(AppxBaseline baseline, bool onlyIfMissing = false)
    {
        lock (_gate)
        {
            var baselines = Load();
            var index = baselines.FindIndex(item => string.Equals(item.Key, baseline.Key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && onlyIfMissing)
            {
                return false;
            }

            if (index >= 0)
            {
                baselines[index] = baseline;
            }
            else
            {
                baselines.Add(baseline);
            }

            ProtectedFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(new StoredFile(SchemaVersion, baselines), Json));
            return true;
        }
    }

    private List<AppxBaseline> Load()
    {
        if (_baselines is null)
        {
            _baselines = File.Exists(_path)
                ? JsonSerializer.Deserialize<StoredFile>(File.ReadAllBytes(_path), Json)?.Baselines.ToList() ?? []
                : [];
        }

        return _baselines;
    }

    private sealed record StoredFile(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("baselines")] IReadOnlyList<AppxBaseline> Baselines);
}
