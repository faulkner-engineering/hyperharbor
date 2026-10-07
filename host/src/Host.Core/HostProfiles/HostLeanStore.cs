using System.Text.Json;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Core.HostProfiles;

/// <summary>The restore point and registry export made before the first apply, and the idle numbers at that time.</summary>
public sealed record HostLeanBaseline(DateTimeOffset At, string RestorePoint, string ExportFolder, HostLeanMetrics? Idle);

/// <summary>A dry run that an apply can rely on while it is recent and the host still gives the same fingerprint.</summary>
public sealed record StoredDryRun(string Source, string ProfileHash, string Fingerprint, DateTimeOffset At, HostLeanPlanSummary Summary);

/// <param name="Baseline">Set by the first successful apply.</param>
/// <param name="ApprovedProfileHash">The Lean profile a person applied last; the monthly re-apply runs only while the shipped profile is still that one.</param>
/// <param name="LastApplyAt">The last time the Lean profile was applied (by a person or the schedule); the monthly clock starts here.</param>
public sealed record HostLeanState(
    HostLeanBaseline? Baseline = null,
    bool ScheduleEnabled = false,
    DateTimeOffset? LastApplyAt = null,
    string? ApprovedProfileHash = null,
    StoredDryRun? DryRun = null,
    HostLeanRunSummary? LastRun = null,
    bool UndoAvailable = false);

/// <summary>
/// The Lean host action's files in the data directory: host-lean\state.json, the undo profile (undo.yaml), and
/// one backup folder per first apply. State is rewritten whole through a temporary file, so a crash never leaves half of it.
/// </summary>
public sealed class HostLeanStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _folder = Path.Combine(dataDirectory, "host-lean");
    private readonly object _gate = new();
    private HostLeanState? _state;

    public string Folder => _folder;

    private string StatePath => Path.Combine(_folder, "state.json");

    private string UndoPath => Path.Combine(_folder, "undo.yaml");

    public HostLeanState Load()
    {
        lock (_gate)
        {
            return _state ??= Read();
        }
    }

    public HostLeanState Update(Func<HostLeanState, HostLeanState> change)
    {
        lock (_gate)
        {
            _state = change(_state ??= Read());
            Directory.CreateDirectory(_folder);
            var temporary = StatePath + ".tmp";
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(_state, JsonOptions));
            File.Move(temporary, StatePath, overwrite: true);
            return _state;
        }
    }

    public string? ReadUndo() => File.Exists(UndoPath) ? File.ReadAllText(UndoPath) : null;

    public void WriteUndo(string yaml)
    {
        Directory.CreateDirectory(_folder);
        var temporary = UndoPath + ".tmp";
        File.WriteAllText(temporary, yaml);
        File.Move(temporary, UndoPath, overwrite: true);
    }

    public void DeleteUndo()
    {
        if (File.Exists(UndoPath))
        {
            File.Delete(UndoPath);
        }
    }

    /// <summary>A new folder for the registry export of a first apply.</summary>
    public string NewBackupFolder(DateTimeOffset now)
    {
        var folder = Path.Combine(_folder, "backup", now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private HostLeanState Read()
    {
        try
        {
            return File.Exists(StatePath) ? JsonSerializer.Deserialize<HostLeanState>(File.ReadAllBytes(StatePath), JsonOptions) ?? new HostLeanState() : new HostLeanState();
        }
        catch (JsonException)
        {
            // A damaged file is treated as no history; the restore point and backup folders stay on disk.
            return new HostLeanState();
        }
    }
}
