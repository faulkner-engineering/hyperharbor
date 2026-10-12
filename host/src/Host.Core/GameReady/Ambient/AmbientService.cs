using System.Text.Json;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.GameReady.Ambient;

public sealed class AmbientStore(string dataDirectory) : IAmbientStore
{
    public const string FileName = "ambient.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _path = Path.Combine(dataDirectory, FileName);

    public AmbientSettings? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AmbientSettings>(Utf8Json.ReadFile(_path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(AmbientSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        var partial = _path + ".partial";
        File.WriteAllBytes(partial, JsonSerializer.SerializeToUtf8Bytes(settings, Options));
        File.Move(partial, _path, overwrite: true);
    }
}

/// <summary>
/// The screensaver file: root\HyperHarbor Ambient.scr beside the helper, a copy of the current version's executable.
/// Windows starts a screensaver as the signed-in user, who cannot write to Program Files, so the service installs it.
/// It is refreshed when its size or time differs from the current executable.
/// </summary>
public sealed class AmbientScreensaverFile(InstallLayout layout) : IAmbientScreensaverFile
{
    private const string UnsupportedText = "The ambient screen needs the installed host (the HyperHarborHost service).";

    public string? UnsupportedReason() => File.Exists(layout.CurrentExecutable) ? null : UnsupportedText;

    public string? CurrentPath() =>
        File.Exists(layout.CurrentExecutable) && IsCurrent(layout.CurrentExecutable) ? layout.AmbientScreensaver : null;

    public string Ensure()
    {
        var source = layout.CurrentExecutable;
        if (!File.Exists(source))
        {
            throw new AmbientException(UnsupportedText);
        }

        if (IsCurrent(source))
        {
            return layout.AmbientScreensaver;
        }

        try
        {
            layout.InstallAmbientScreensaver(source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AmbientException($"The screensaver file could not be installed: {ex.Message}");
        }

        return layout.AmbientScreensaver;
    }

    private bool IsCurrent(string source)
    {
        var target = new FileInfo(layout.AmbientScreensaver);
        var original = new FileInfo(source);
        return target.Exists && target.Length == original.Length && target.LastWriteTimeUtc == original.LastWriteTimeUtc;
    }
}

/// <summary>Saves the Ambient screen choices and makes sure the screensaver file is in place while they are on.</summary>
public sealed class AmbientService(IAmbientStore store, IAmbientScreensaverFile file, ILogger<AmbientService> logger)
{
    private readonly object _gate = new();

    public AmbientSettings Settings => store.Load() ?? AmbientSettings.Default;

    public AmbientReading Read(CecDevice? cec)
    {
        var settings = Settings;
        var reason = file.UnsupportedReason();
        return new AmbientReading(settings, reason is null, reason, reason is null ? file.CurrentPath() : null, cec?.Name);
    }

    /// <summary>Validates against the power mode, installs the file when the screen is on, and saves.</summary>
    /// <exception cref="AmbientException">The choices are not valid or the file cannot be installed.</exception>
    public void Apply(AmbientSettings settings, PowerModeSettings power)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings with { Chord = AmbientChord.Normalize(settings.Chord) ?? settings.Chord };
        if (normalized.Validate(power) is { } invalid)
        {
            throw new AmbientException(invalid);
        }

        lock (_gate)
        {
            if (normalized.Enabled)
            {
                file.Ensure();
            }

            store.Save(normalized);
            logger.LogInformation("Ambient screen {State}, starting after {Minutes} minutes.", normalized.Enabled ? "on" : "off", normalized.IdleMinutes);
        }
    }

    /// <summary>Called when the service starts (also after an update): brings the file up to date while the screen is on.</summary>
    public void Refresh()
    {
        if (!Settings.Enabled)
        {
            return;
        }

        try
        {
            file.Ensure();
        }
        catch (AmbientException ex)
        {
            logger.LogWarning("The ambient screensaver file could not be refreshed: {Reason}", ex.Message);
        }
    }

    /// <summary>The reason a new display timeout would break the ordering with the screen, or null.</summary>
    public string? DisplayTimeoutProblem(PowerModeSettings power)
    {
        ArgumentNullException.ThrowIfNull(power);
        var settings = Settings;
        return settings.Enabled && power.Mode == PowerModes.ConsoleIdle
            ? AmbientTimeouts.OrderProblem(settings.IdleMinutes * 60, power.DisplayMinutes * 60)
            : null;
    }
}

/// <summary>An HDMI-CEC adapter found on this PC. Standby over CEC is not built; only detection is.</summary>
public sealed record CecDevice(string Name);

/// <summary>Finds a USB HDMI-CEC adapter among the Plug and Play devices (Pulse-Eight, vendor 2548).</summary>
public static class CecDetector
{
    public static CecDevice? Find(IEnumerable<PnpNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var node = nodes.FirstOrDefault(node => node.InstanceId.StartsWith(@"USB\VID_2548&PID_", StringComparison.OrdinalIgnoreCase));
        return node is null ? null : new CecDevice(node.Name);
    }
}
