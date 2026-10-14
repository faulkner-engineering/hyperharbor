using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.GameAware;

/// <summary>What the host does with a running VM while a game is in the foreground.</summary>
public enum GamePolicy
{
    /// <summary>Freeze the VM in memory. The default, and the only choice for a VM with a GPU partition.</summary>
    Pause,

    /// <summary>Save the VM's memory to disk and stop it. It is started again afterwards.</summary>
    Save,

    /// <summary>Leave the VM running.</summary>
    Keep,
}

/// <param name="AlwaysPause">Apply the policy even while someone is connected to the VM.</param>
public sealed record GameVmPolicy(GamePolicy Policy = GamePolicy.Pause, bool AlwaysPause = false);

/// <summary>
/// Game-aware mode choices in game-aware.json. Process names are lower case without ".exe". Nothing here is secret.
/// </summary>
public sealed record GameAwareSettings
{
    public bool Enabled { get; init; }

    public GamePolicy DefaultPolicy { get; init; } = GamePolicy.Pause;

    /// <summary>Per VM, keyed by the VM id in "D" format.</summary>
    public IReadOnlyDictionary<string, GameVmPolicy> VmPolicies { get; init; } = new Dictionary<string, GameVmPolicy>();

    /// <summary>Programs that count as a game whenever they are in the foreground.</summary>
    public IReadOnlyList<string> Allowlist { get; init; } = [];

    /// <summary>Programs that never count (false positives such as a presentation or a video player).</summary>
    public IReadOnlyList<string> IgnoreList { get; init; } = [];

    /// <summary>Launchers added by the user, besides the built-in ones. A game started by one counts; its own window does not.</summary>
    public IReadOnlyList<string> ExtraLaunchers { get; init; } = [];

    public GameVmPolicy PolicyFor(Guid vmId) =>
        VmPolicies.TryGetValue(vmId.ToString("D"), out var own) ? own : new GameVmPolicy(DefaultPolicy);

    /// <summary>The name as stored: trimmed, lower case, no folder, no ".exe".</summary>
    public static string NormalizeProcess(string name)
    {
        var trimmed = name.Trim().Replace('\\', '/');
        var file = trimmed[(trimmed.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return file.EndsWith(".exe", StringComparison.Ordinal) ? file[..^4] : file;
    }
}

/// <summary>Game-aware settings in game-aware.json, written atomically.</summary>
public sealed class GameAwareStore
{
    public const string FileName = "game-aware.json";
    public const int MaxListEntries = 200;

    private static readonly JsonSerializerOptions Json = Create();

    private readonly string _path;
    private readonly object _gate = new();

    public GameAwareStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public GameAwareSettings Load()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    public GameAwareSettings Update(Func<GameAwareSettings, GameAwareSettings> change)
    {
        lock (_gate)
        {
            var updated = Clean(change(Read()));
            Write(updated);
            return updated;
        }
    }

    /// <summary>Process names are normalized and de-duplicated, and a program cannot be on both lists (the newer list wins in <see cref="GameAwareController"/>).</summary>
    private static GameAwareSettings Clean(GameAwareSettings settings) => settings with
    {
        Allowlist = CleanList(settings.Allowlist),
        IgnoreList = CleanList(settings.IgnoreList),
        ExtraLaunchers = CleanList(settings.ExtraLaunchers),
    };

    private static List<string> CleanList(IEnumerable<string> names) =>
        names.Select(GameAwareSettings.NormalizeProcess).Where(name => name.Length > 0).Distinct().Take(MaxListEntries).ToList();

    private GameAwareSettings Read()
    {
        if (!File.Exists(_path))
        {
            return new GameAwareSettings();
        }

        return JsonSerializer.Deserialize<GameAwareSettings>(Utf8Json.ReadFile(_path), Json) ?? new GameAwareSettings();
    }

    private void Write(GameAwareSettings settings) => AtomicJson.Write(_path, settings, Json);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

/// <summary>Writes a JSON file through a temporary file, so a crash never leaves half a file.</summary>
internal static class AtomicJson
{
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, options));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
