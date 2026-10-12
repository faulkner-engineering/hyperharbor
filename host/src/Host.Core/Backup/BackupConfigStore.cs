using System.Text.Json;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>The persisted backup configuration. Holds no secrets: keys are references into <see cref="IBackupSecretStore"/>.</summary>
public sealed record BackupConfig
{
    public IReadOnlyList<BackupTarget> Targets { get; init; } = [];

    public IReadOnlyList<BackupPolicy> Policies { get; init; } = [];
}

/// <summary>Targets and per-VM policies in backup-config.json, written atomically.</summary>
public sealed class BackupConfigStore
{
    public const string FileName = "backup-config.json";

    private readonly string _path;
    private readonly object _gate = new();

    public BackupConfigStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public BackupConfig Load()
    {
        lock (_gate)
        {
            return Read();
        }
    }

    public BackupConfig Update(Func<BackupConfig, BackupConfig> change)
    {
        lock (_gate)
        {
            var updated = change(Read());
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(updated, BackupJson.Options));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }

            return updated;
        }
    }

    public BackupTarget? FindTarget(string id) => Load().Targets.FirstOrDefault(t => t.Id == id);

    private BackupConfig Read() =>
        File.Exists(_path)
            ? JsonSerializer.Deserialize<BackupConfig>(Utf8Json.ReadFile(_path), BackupJson.Options) ?? new BackupConfig()
            : new BackupConfig();
}
