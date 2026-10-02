using System.Text.Json;

namespace HyperHarbor.Host.Core.Identity;

/// <summary>
/// Loads the host's stable identifier, creating it on first run.
/// </summary>
public sealed class HostIdentityStore
{
    private const string FileName = "host-identity.json";

    private readonly string _path;
    private readonly object _gate = new();
    private Guid? _hostId;

    public HostIdentityStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    /// <summary>Default data directory: %ProgramData%\HyperHarbor.</summary>
    public static string DefaultDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperHarbor");

    public Guid GetOrCreateHostId()
    {
        lock (_gate)
        {
            return _hostId ??= LoadOrCreate();
        }
    }

    private Guid LoadOrCreate()
    {
        if (File.Exists(_path))
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(_path));
            if (stored is { HostId: var id } && id != Guid.Empty)
            {
                return id;
            }

            throw new InvalidDataException($"The host identity file '{_path}' is invalid. Delete it to generate a new host ID.");
        }

        var created = new StoredIdentity(Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // Write to a temporary file first so a crash cannot leave a partial identity file.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(created));
        File.Move(temporary, _path, overwrite: false);
        return created.HostId;
    }

    private sealed record StoredIdentity(Guid HostId);
}
