using System.Text.Json;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>
/// The names the tray and the ambient screensaver process share. Events and the mutex live in the session's Local
/// namespace, so they reach the screensaver of the same desktop only.
/// </summary>
internal static class AmbientNames
{
    public const string RunningMutex = @"Local\HyperHarbor.Ambient.Running";
    public const string DismissEvent = @"Local\HyperHarbor.Ambient.Dismiss";
    public const string HintEvent = @"Local\HyperHarbor.Ambient.Hint";

    /// <summary>The screensaver file's name without its extension: the process name Windows shows.</summary>
    public const string ProcessName = "HyperHarbor Ambient";

    /// <summary>%LOCALAPPDATA%\HyperHarbor\ambient: what the tray tells the screen, and what the screen measured.</summary>
    public static string Folder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarbor", "ambient");

    public static string ScreenDataFile => Path.Combine(Folder, "screen.json");

    public static string MetricsFile => Path.Combine(Folder, "metrics.json");

    public static string PreviousScreensaverFile => Path.Combine(Folder, "previous-screensaver.json");
}

/// <summary>The tray's view of the running screensaver process, through the named mutex and events.</summary>
internal sealed class AmbientSignals : IAmbientSignals
{
    public bool Running
    {
        get
        {
            if (!Mutex.TryOpenExisting(AmbientNames.RunningMutex, out var mutex))
            {
                return false;
            }

            mutex.Dispose();
            return true;
        }
    }

    public void Dismiss() => Set(AmbientNames.DismissEvent);

    public void ShowHint() => Set(AmbientNames.HintEvent);

    private static void Set(string name)
    {
        if (EventWaitHandle.TryOpenExisting(name, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }
}

/// <summary>What the page of the ambient screen shows. The tray writes it; the screensaver reads it about once a second.</summary>
/// <param name="HostStatus">One line on the host, or null when the widget is off.</param>
/// <param name="RunningVms">Null when the widget is off.</param>
/// <param name="Devices">Null when the widget is off.</param>
/// <param name="Backup">One muted line about the backups (shown with the host status); null when the widget is off or nothing is backed up.</param>
/// <param name="Gaming">One muted line while a game is in the foreground (shown with the host status); null otherwise.</param>
/// <param name="ChordHint">The unlock chord, for example "LB + RB + Start"; null when the soft lock is off.</param>
internal sealed record AmbientScreenData(string? HostStatus, IReadOnlyList<string>? RunningVms, string? Devices, string? ChordHint, string? Backup = null, string? Gaming = null);

/// <summary>What the screensaver measured about itself while it ran.</summary>
internal sealed record AmbientMetrics(DateTimeOffset At, double CpuPercent, double? GpuPercent, int Seconds);

/// <summary>Small JSON files in <see cref="AmbientNames.Folder"/>. Written whole to a temporary file and moved into place.</summary>
internal static class AmbientFiles
{
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var partial = path + ".partial";
        File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(value, ContractJson.Options));
        File.Move(partial, path, overwrite: true);
    }

    /// <returns>The value, or null when the file is missing, being replaced, or not valid.</returns>
    public static T? Read<T>(string path)
        where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), ContractJson.Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
