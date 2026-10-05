using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>Where an update stands. The helper saves each phase before starting it, so a restart can resume.</summary>
public enum UpdatePhase
{
    /// <summary>No update in progress. LastResult says how the last one ended.</summary>
    Idle,

    /// <summary>The service asked the helper to install StagedExecutable as To.</summary>
    HandingOff,

    Stopping,
    BackingUp,
    Flipping,

    /// <summary>The new version is being started (Attempt of the allowed attempts). It may have migrated the data.</summary>
    Starting,

    RollingBack,

    /// <summary>Even the previous version did not start after the rollback; someone needs to look.</summary>
    Failed,
}

public sealed record UpdateState
{
    public UpdatePhase Phase { get; init; } = UpdatePhase.Idle;

    public string? From { get; init; }

    public string? To { get; init; }

    public string? StagedExecutable { get; init; }

    public string? Backup { get; init; }

    public int Attempt { get; init; }

    /// <summary>Versions that failed to start here and were rolled back; they are not offered again.</summary>
    public IReadOnlyList<string> RolledBack { get; init; } = [];

    public string? LastResult { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>The state after an update ends: idle, with the outcome and nothing in progress.</summary>
    public UpdateState Finished(string result, DateTimeOffset now) =>
        new() { RolledBack = RolledBack, LastResult = result, UpdatedAt = now };
}

/// <summary>update\state.json in the data directory, written atomically with the restricted ACL.</summary>
public sealed class UpdateStateStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, DataBackup.UpdateFolderName, "state.json");

    public UpdateState Load() =>
        File.Exists(Path) ? JsonSerializer.Deserialize<UpdateState>(Utf8Json.ReadFile(Path), JsonOptions) ?? new UpdateState() : new UpdateState();

    public void Save(UpdateState state) => ProtectedFile.WriteAllBytes(Path, JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions));
}
