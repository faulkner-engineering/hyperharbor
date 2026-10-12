using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.Ambient;

/// <summary>
/// The tray's part of the Ambient screen: it makes the desktop's screensaver the ambient screen (and gives the user's
/// back), ends the screen on controller input, keeps the screen's data file current, and passes the screen's measured cost
/// and the screensaver state to the service. Called from the UI thread: <see cref="Tick"/> four times a second.
/// </summary>
internal sealed class AmbientCoordinator
{
    private const string ConsoleIdleMode = "consoleIdle";

    /// <summary>The widgets' data is written, and the screen's cost read, once per this many ticks (a second).</summary>
    public const int TicksPerSecond = 4;

    /// <summary>The service is asked for the VM list at most this often (in seconds) while the screen runs.</summary>
    public const int SnapshotSeconds = 10;

    private readonly AmbientScreensaverApplier _applier;
    private readonly AmbientDismissal _dismissal;
    private readonly IAmbientSignals _signals;
    private readonly Action<TrayMessage> _send;
    private readonly Func<string, AmbientMetrics?> _readMetrics;
    private readonly Action<AmbientScreenData> _writeData;
    private readonly string _metricsPath;

    private AmbientInfo? _info;
    private AmbientScreensaverState? _reported;
    private AmbientMetrics? _metrics;
    private AmbientMetrics? _reportedMetrics;
    private AmbientSnapshotMessage? _snapshot;
    private AmbientScreenData? _written;
    private string _hostStatus = "Host service offline";
    private bool _wasRunning;
    private int _ticks;
    private int _secondsSinceSnapshot;

    public AmbientCoordinator(
        AmbientScreensaverApplier applier,
        AmbientDismissal dismissal,
        IAmbientSignals signals,
        Action<TrayMessage> send,
        Func<string, AmbientMetrics?> readMetrics,
        Action<AmbientScreenData> writeData,
        string metricsPath)
    {
        _applier = applier;
        _dismissal = dismissal;
        _signals = signals;
        _send = send;
        _readMetrics = readMetrics;
        _writeData = writeData;
        _metricsPath = metricsPath;
    }

    /// <summary>The service's answer for the widgets.</summary>
    public void OnSnapshot(AmbientSnapshotMessage snapshot) => _snapshot = snapshot;

    /// <summary>The display turned off or on (the change to on starts the grace period of the dismissal).</summary>
    public void SetDisplayOff(bool off) => _dismissal.DisplayOff = off;

    public void SetHostStatus(string text) => _hostStatus = text;

    /// <summary>The service connected again: it has forgotten what the tray reported.</summary>
    public void Reconnected()
    {
        _reported = null;
        _reportedMetrics = null;
    }

    /// <summary>A new Game ready status: applies the screensaver, and tells the dismissal what is on.</summary>
    public void OnStatus(GameReadyStatus? status)
    {
        var info = status?.Ambient;
        _info = info;
        if (info is null)
        {
            // Without the service the tray knows nothing: leave the desktop as it is.
            _dismissal.Enabled = false;
            return;
        }

        var wanted = info.Enabled && info.Supported && info.ScreensaverPath is not null && status!.PowerMode?.Mode == ConsoleIdleMode;
        _dismissal.Enabled = wanted;
        _dismissal.SoftLock = info.SoftLock;
        _dismissal.Chord = AmbientChord.Parse(info.Chord) ?? 0;

        AmbientScreensaverState state;
        try
        {
            state = _applier.Sync(wanted ? info.ScreensaverPath : null, info.IdleMinutes * 60);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // The registry or the backup file refused: report what is there, so the card shows the mismatch.
            state = info.Screensaver ?? new AmbientScreensaverState(false, null, 0, false);
        }

        Report(state);
    }

    /// <summary>Four times a second: controller dismissal; once a second: data for the screen and its measured cost.</summary>
    public void Tick()
    {
        _dismissal.Tick();
        if (++_ticks % TicksPerSecond != 0)
        {
            return;
        }

        ReadMetrics();
        var running = _signals.Running;
        if (running && (!_wasRunning || ++_secondsSinceSnapshot >= SnapshotSeconds))
        {
            _secondsSinceSnapshot = 0;
            _send(new AmbientSnapshotQueryMessage());
        }

        _wasRunning = running;
        if (_info is { Enabled: true })
        {
            WriteData();
        }
    }

    private void ReadMetrics()
    {
        var metrics = _readMetrics(_metricsPath);
        if (metrics is null || metrics == _metrics)
        {
            return;
        }

        _metrics = metrics;
        if (_reported is { } state)
        {
            Report(state);
        }
    }

    private void Report(AmbientScreensaverState state)
    {
        if (state == _reported && _metrics == _reportedMetrics)
        {
            return;
        }

        _reported = state;
        _reportedMetrics = _metrics;
        _send(new AmbientReportMessage(state, _metrics?.CpuPercent, _metrics?.GpuPercent, _metrics?.Seconds));
    }

    private void WriteData()
    {
        var info = _info!;
        var snapshot = _snapshot;
        var data = new AmbientScreenData(
            info.ShowHostStatus ? _hostStatus : null,
            info.ShowRunningVms ? snapshot?.RunningVms ?? [] : null,
            info.ShowDevices && snapshot is not null ? DeviceText(snapshot) : null,
            info.SoftLock ? AmbientChord.Describe(info.Chord) : null);
        if (_written is not null && Same(_written, data))
        {
            return;
        }

        _written = data;
        try
        {
            _writeData(data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Written again on the next change; the screen shows its last data meanwhile.
            _written = null;
        }
    }

    internal static string DeviceText(AmbientSnapshotMessage snapshot) =>
        $"{snapshot.PairedDevices} paired {(snapshot.PairedDevices == 1 ? "device" : "devices")}{(snapshot.InUse ? ", in use now" : string.Empty)}";

    private static bool Same(AmbientScreenData a, AmbientScreenData b) =>
        a.HostStatus == b.HostStatus
        && a.Devices == b.Devices
        && a.ChordHint == b.ChordHint
        && (a.RunningVms is null ? b.RunningVms is null : b.RunningVms is not null && a.RunningVms.SequenceEqual(b.RunningVms));
}
