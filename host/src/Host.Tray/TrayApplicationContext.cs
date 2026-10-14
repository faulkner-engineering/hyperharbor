using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using HyperHarbor.Host.Tray.Ambient;
using HyperHarbor.Host.Tray.Window;
using HyperHarbor.Shared.Contracts.Hosts;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Web.WebView2.Core;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Owns the notification area icon and its menu, the pipe to the service, and the two HTML windows (the host window
/// and the pairing PIN window, both <see cref="WebWindow"/>). Every change goes to the windows as one
/// <see cref="TrayViewState"/>; what the user does there comes back as <see cref="ITrayActions"/> calls.
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext, ITrayActions
{
    /// <summary>Where the service keeps its data unless DataDirectory is configured.</summary>
    public static readonly string DataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "HyperHarbor");

    /// <summary>A check the service never reports ends after this long, so the page stops waiting.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A hidden host window is kept this long, so opening it again is instant, then released.</summary>
    private static readonly TimeSpan KeepHiddenWindow = TimeSpan.FromMinutes(10);

    private readonly NotifyIcon _notifyIcon;
    private readonly TrayIconProvider _iconProvider;
    private readonly ToolStripMenuItem _status;
    private readonly ToolStripMenuItem _passphraseItem;
    private readonly TrayPipeClient _pipe;
    private readonly ShutdownGuard _shutdownGuard;
    private readonly System.Windows.Forms.Timer _gpuVmPoll;
    private readonly System.Windows.Forms.Timer _updatePoll;
    private readonly System.Windows.Forms.Timer _inputPoll;
    private readonly System.Windows.Forms.Timer _gamePoll;
    private readonly DisplayWake _displayWake;
    private readonly DisplayStateWatcher _displayWatcher;
    private readonly AmbientCoordinator _ambient;
    private readonly System.Windows.Forms.Timer _releaseHostWindow;
    private readonly HashSet<string> _busy = [];
    private readonly bool _webViewAvailable = TrayWebView.RuntimeVersion is not null;

    private WebWindow? _hostWindow;
    private WebWindow? _pinWindow;
    private bool _connected;
    private bool? _passphraseConfigured;
    private IReadOnlyList<TrayDevice> _devices = [];
    private string? _isoFolder;
    private VmFolderMessage? _vmFolder;
    private string? _backupFolder;
    private IReadOnlyList<string> _gpuVms = [];
    private HostUpdateStatus? _update;
    private TrayBackupStatus? _backup;
    private GameAwareInfo? _gameAware;
    private RestoreWizardStatus? _restoreWizard;
    private int _gameAwareTicks;
    private readonly Backup.BackupNotifier _backupNotifier = new();
    private int _backupTicks;
    private bool _restarting;
    private const string ConsoleIdleMode = "consoleIdle";
    private HostLeanStatus? _hostLean;
    private GameReadyStatus? _gameReady;
    private PairingStartedMessage? _pairing;
    private (DateTimeOffset? LastCheck, DateTimeOffset StartedAt)? _pendingCheck;
    private int _pollTicks;

    public TrayApplicationContext()
    {
        _status = new ToolStripMenuItem("Connecting to host service…") { Enabled = false };

        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Open HyperHarbor Host…", null, (_, _) => ShowHost()) { Font = new Font(menu.Font, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Paired devices…", null, (_, _) => ShowHost("devices"));
        _passphraseItem = new ToolStripMenuItem("Set admin passphrase…", null, (_, _) => ShowHost("passphrase")) { Enabled = false };
        menu.Items.Add(_passphraseItem);
        if (!_webViewAvailable)
        {
            menu.Items.Add("Install the WebView2 Runtime…", null, (_, _) => Launch(TrayWebView.DownloadUrl));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sleep now…", null, (_, _) => ConfirmSleepNow());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        // Asleep until the service answers on the pipe.
        _iconProvider = new TrayIconProvider(TrayIconState.Asleep);
        _notifyIcon = new NotifyIcon
        {
            Icon = _iconProvider.Current,
            Text = "HyperHarbor",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _notifyIcon.DoubleClick += (_, _) => ShowHost();
        _iconProvider.IconChanged += (_, _) => _notifyIcon.Icon = _iconProvider.Current;

        _pipe = new TrayPipeClient();
        _pipe.ConnectionChanged += OnConnectionChanged;
        _pipe.MessageReceived += OnMessage;
        _shutdownGuard = new ShutdownGuard(() => _connected ? _gpuVms : [], () => _ = _pipe.SendAsync(new StopGpuVmsMessage()));

        // The guard must answer a shutdown at once, so it uses the last known list rather than asking then.
        _gpuVmPoll = new System.Windows.Forms.Timer { Interval = 30_000 };
        _gpuVmPoll.Tick += (_, _) => QueryGpuVms();
        _gpuVmPoll.Start();

        // Update progress changes on its own, so the open window asks for it: every second while something moves.
        _updatePoll = new System.Windows.Forms.Timer { Interval = 1_000 };
        _updatePoll.Tick += (_, _) => PollUpdate();
        _updatePoll.Start();

        // Console idle turns the display off; a controller press must bring it back even when Windows does not count it.
        var controllers = new XInputControllers();
        _displayWake = new DisplayWake(controllers, new ExecutionStateDisplay(), TimeProvider.System);

        // The ambient screen is a screensaver process; the tray sets it up, ends it on controller input, and feeds its page.
        var signals = new AmbientSignals();
        _ambient = new AmbientCoordinator(
            new AmbientScreensaverApplier(new WindowsScreensaverSettings(), new FileScreensaverBackup(AmbientNames.PreviousScreensaverFile)),
            new AmbientDismissal(controllers, signals, TimeProvider.System),
            signals,
            message =>
            {
                if (_connected)
                {
                    _ = _pipe.SendAsync(message);
                }
            },
            AmbientFiles.Read<AmbientMetrics>,
            data => AmbientFiles.Write(AmbientNames.ScreenDataFile, data),
            AmbientNames.MetricsFile);
        _displayWatcher = new DisplayStateWatcher(off =>
        {
            _displayWake.DisplayOff = off;
            _ambient.SetDisplayOff(off);
        });
        _inputPoll = new System.Windows.Forms.Timer { Interval = 250 };
        _inputPoll.Tick += (_, _) =>
        {
            _displayWake.Tick();
            _ambient.Tick();
        };
        _inputPoll.Start();

        // Only this desktop can see the foreground. The service decides whether it is a game: game-aware mode, the
        // scheduled sleep, and the backup conditions all use that answer.
        _gamePoll = new System.Windows.Forms.Timer { Interval = GameSignalSeconds * 1_000 };
        _gamePoll.Tick += (_, _) => SendGameSignals();
        _gamePoll.Start();

        _releaseHostWindow = new System.Windows.Forms.Timer { Interval = (int)KeepHiddenWindow.TotalMilliseconds };
        _releaseHostWindow.Tick += (_, _) =>
        {
            _releaseHostWindow.Stop();
            if (_hostWindow is { Visible: false })
            {
                _hostWindow.Dispose();
                _hostWindow = null;
            }
        };

        _pipe.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gpuVmPoll.Dispose();
            _updatePoll.Dispose();
            _inputPoll.Dispose();
            _gamePoll.Dispose();
            _displayWatcher.Dispose();
            _releaseHostWindow.Dispose();
            _shutdownGuard.Dispose();
            _pipe.Dispose();
            _hostWindow?.Dispose();
            _pinWindow?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _iconProvider.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Opens the host window, optionally at a page (devices, passphrase, ...).</summary>
    internal void ShowHost(string? page = null)
    {
        if (!_webViewAvailable)
        {
            _notifyIcon.ShowBalloonTip(10_000, "HyperHarbor needs the WebView2 Runtime", "Choose Install the WebView2 Runtime in this menu, then open HyperHarbor Host again.", ToolTipIcon.Warning);
            return;
        }

        _releaseHostWindow.Stop();
        if (_hostWindow is null)
        {
            var window = new WebWindow("HyperHarbor Host", "host", new Size(980, 660), new Size(720, 500)) { HideOnClose = true };
            window.PlaceOn(Screen.FromPoint(Cursor.Position));
            window.MessageReceived += OnPageMessage;
            window.Hidden += () => _releaseHostWindow.Start();
            _hostWindow = window;
            _ = InitializeAsync(window);
            PushState();
            if (_connected)
            {
                _ = _pipe.SendAsync(new ListDevicesMessage());
                _ = _pipe.SendAsync(new UpdateStatusQueryMessage());
                _ = _pipe.SendAsync(new BackupStatusQueryMessage());
                _ = _pipe.SendAsync(new HostLeanQueryMessage());
                _ = _pipe.SendAsync(new GameReadyQueryMessage());
                _ = _pipe.SendAsync(new RestoreWizardQueryMessage());
            }
        }

        if (page is not null)
        {
            _hostWindow.Post(new HostToPage.Navigate(page));
        }

        _hostWindow.ShowWhenReady();
    }

    private async Task InitializeAsync(WebWindow window)
    {
        try
        {
            await window.InitializeAsync(await TrayWebView.EnvironmentAsync());
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or WebView2RuntimeNotFoundException)
        {
            _notifyIcon.ShowBalloonTip(10_000, "HyperHarbor could not open its window", ex.Message, ToolTipIcon.Warning);
            if (ReferenceEquals(window, _hostWindow))
            {
                _hostWindow = null;
            }
            else if (ReferenceEquals(window, _pinWindow))
            {
                _pinWindow = null;
            }

            window.Dispose();
        }
    }

    private bool HostWindowOpen => _hostWindow is { Visible: true, IsDisposed: false };

    private void OnPageMessage(JsonElement message) => TrayCommands.Dispatch(message, this);

    /// <summary>Sends the current state to the open windows.</summary>
    private void PushState()
    {
        var state = new HostToPage.State(new TrayViewState(
            _connected,
            _passphraseConfigured,
            _devices.Select(device => new TrayViewDevice(device.DeviceId, device.Name, device.CertificateFingerprint, device.PairedAt)).ToList(),
            _vmFolder is null ? null : new TrayViewFolder(_vmFolder.Folder, _vmFolder.IsDefault),
            _isoFolder,
            _backupFolder,
            ConsoleAccessSetup.IsSetUp(DataDirectory),
            PackageSearchSetupHelper.IsSetUp,
            _connected ? _update : null,
            _connected ? _hostLean : null,
            _connected ? _gameReady : null,
            _connected ? _backup : null,
            _connected ? _gameAware : null,
            _connected ? _restoreWizard : null,
            _busy.Order(StringComparer.Ordinal).ToList(),
            _pairing is null ? null : new TrayViewPairing(_pairing.PairingId, _pairing.DeviceName, _pairing.Pin, _pairing.ExpiresAt),
            DataDirectory));
        _hostWindow?.Post(state);
        _pinWindow?.Post(state);
    }

    /// <summary>In the host window when it is open, otherwise as a notification.</summary>
    private void Notify(string kind, string title, string text)
    {
        if (HostWindowOpen)
        {
            _hostWindow!.Post(new HostToPage.Toast(kind, title, text));
            return;
        }

        _notifyIcon.ShowBalloonTip(kind == "error" ? 15_000 : 5_000, title, text, kind == "error" ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void SetBusy(string key, bool busy)
    {
        if (busy ? _busy.Add(key) : _busy.Remove(key))
        {
            PushState();
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        _status.Text = connected ? "Host service running" : "Host service not running";
        _notifyIcon.Text = connected ? "HyperHarbor" : "HyperHarbor (service not running)";
        _passphraseItem.Enabled = connected;
        _connected = connected;
        _ambient.SetHostStatus(connected ? "Host is running" : "Host service offline");
        if (connected)
        {
            _ambient.Reconnected();
        }

        // The service does not report sessions to the tray yet, so the icon shows only awake or asleep.
        _iconProvider.State = connected ? TrayIconState.Awake : TrayIconState.Asleep;
        if (connected)
        {
            QueryGpuVms();
            _ = _pipe.SendAsync(new BackupStatusQueryMessage());
            _ = _pipe.SendAsync(new GameAwareQueryMessage());
            if (_hostWindow is not null)
            {
                _ = _pipe.SendAsync(new ListDevicesMessage());
                _ = _pipe.SendAsync(new UpdateStatusQueryMessage());
                _ = _pipe.SendAsync(new BackupStatusQueryMessage());
                _ = _pipe.SendAsync(new HostLeanQueryMessage());
                _ = _pipe.SendAsync(new GameReadyQueryMessage());
                _ = _pipe.SendAsync(new RestoreWizardQueryMessage());
            }
        }
        else
        {
            _passphraseConfigured = null;
            _isoFolder = null;
            _vmFolder = null;
            _backupFolder = null;
            _gpuVms = [];
            _update = null;
            _hostLean = null;
            _gameReady = null;
            _gameAware = null;
            _restoreWizard = null;
            _pendingCheck = null;
            _busy.Clear();
            EndPairing();
        }

        PushState();
    }

    private void OnMessage(TrayMessage message)
    {
        switch (message)
        {
            case PairingStartedMessage started:
                StartPairing(started);
                break;
            case PairingEndedMessage ended:
                OnPairingEnded(ended);
                break;
            case DeviceListMessage list:
                _devices = list.Devices;
                _busy.RemoveWhere(key => key.StartsWith("device:", StringComparison.Ordinal));
                PushState();
                break;
            case VmFolderMessage vmFolder:
                _vmFolder = vmFolder;
                FolderReported("vm", vmFolder.Error, "VM storage changed", $"New VMs are created in {vmFolder.Folder}.");
                break;
            case IsoFolderMessage isoFolder:
                _isoFolder = isoFolder.Folder;
                FolderReported("iso", isoFolder.Error, "ISO library moved", $"New images are stored in {isoFolder.Folder}.");
                break;
            case BackupFolderMessage backupFolder:
                _backupFolder = backupFolder.Folder;
                FolderReported("backup", backupFolder.Error, "Backup folder changed", $"Disk exports go to {backupFolder.Folder}.");
                break;
            case AdminPassphraseStatusMessage status:
                OnPassphraseStatus(status);
                break;
            case GpuVmsMessage gpuVms:
                _gpuVms = gpuVms.Running;
                break;
            case GpuVmsStoppedMessage stopped:
                OnGpuVmsStopped(stopped);
                break;
            case WakeFixRequestedMessage wakeFix:
                _ = ApproveWakeFixAsync(wakeFix);
                break;
            case GameAwareStatusMessage gameAwareStatus:
                OnGameAwareStatus(gameAwareStatus.Info);
                break;
            case BackupRunNowResultMessage runNow:
                Notify(
                    runNow.Started > 0 ? "success" : "error",
                    runNow.Started > 0 ? "Backups started" : "No backup started",
                    runNow.Started > 0
                        ? $"{runNow.Started} backup{(runNow.Started == 1 ? string.Empty : "s")} started.{(runNow.Problems.Count > 0 ? " " + string.Join(" ", runNow.Problems) : string.Empty)}"
                        : runNow.Problems.Count > 0 ? string.Join(" ", runNow.Problems) : "Nothing is set up to back up yet.");
                break;
            case RestoreWizardStatusMessage wizard:
                _restoreWizard = wizard.Status;
                PushState();
                break;
            case BackupStatusMessage backup:
                OnBackupStatus(backup.Status);
                break;
            case UpdateStatusMessage update:
                OnUpdateStatus(update.Status);
                break;
            case HostLeanStatusMessage lean:
                OnHostLeanStatus(lean.Status);
                break;
            case GameReadyStatusMessage gameReady:
                OnGameReadyStatus(gameReady.Status);
                break;
            case AmbientSnapshotMessage snapshot:
                _ambient.OnSnapshot(snapshot);
                break;
        }
    }

    private void FolderReported(string which, string? error, string title, string text)
    {
        if (_busy.Remove("folder:" + which))
        {
            if (error is null)
            {
                Notify("success", title, text);
            }
            else
            {
                Notify("error", "The folder was not changed", error);
            }
        }

        PushState();
    }

    private void OnPassphraseStatus(AdminPassphraseStatusMessage status)
    {
        _passphraseConfigured = status.Configured;
        _passphraseItem.Text = status.Configured ? "Change admin passphrase…" : "Set admin passphrase…";
        if (status.Configured && _busy.Remove("passphrase"))
        {
            Notify("success", "Admin passphrase saved", "Paired devices now need it to change VMs.");
        }

        PushState();
    }

    private void OnUpdateStatus(HostUpdateStatus status)
    {
        // The service was updated while this tray kept running the old version: hand over to the current copy.
        if (!_restarting && TrayRestart.IsStale(status, TrayRestart.OwnVersion))
        {
            _restarting = true;
            if (TrayRestart.TryStartCurrent())
            {
                ExitThread();
                return;
            }
        }

        // In notify mode nothing installs by itself, so say once when a version is ready.
        if (status is { Activity: HostUpdateActivity.Ready, Mode: HostUpdateMode.Notify } && _update?.Activity != HostUpdateActivity.Ready)
        {
            Notify("info", $"HyperHarbor {status.AvailableVersion} is ready", "Open Updates and choose Install now. The host restarts.");
        }

        _update = status;

        // A check this tray asked for ends when the service reports a newer check (older services answer before the
        // check runs and do not report it as checking), or after CheckTimeout.
        if (_pendingCheck is { } pending && status.Activity != HostUpdateActivity.Checking
            && (status.LastCheck != pending.LastCheck || DateTimeOffset.UtcNow - pending.StartedAt > CheckTimeout))
        {
            _pendingCheck = null;
            _busy.Remove("update");
            if (status.LastCheck == pending.LastCheck)
            {
                Notify("error", "The update check did not finish", "The host did not report a result within 90 seconds. Its log has details.");
            }
            else if (status.Message?.StartsWith("The update check failed", StringComparison.Ordinal) == true)
            {
                Notify("error", "The update check failed", status.Message);
            }
            else if (status.AvailableVersion is null)
            {
                Notify("success", "Up to date", $"{status.CurrentVersion} is the newest version on the {status.Channel} channel.");
            }
        }

        PushState();
    }

    private void OnHostLeanStatus(HostLeanStatus status)
    {
        var finished = _hostLean?.Busy is not null && status.Busy is null;
        _hostLean = status;
        PushState();
        if (finished && status.LastRun is { } run)
        {
            Notify(run.Problems.Count == 0 ? "success" : "info", "Lean host", $"{run.Changed} changes made, {run.Problems.Count} problems.");
        }
    }

    private void OnGameReadyStatus(GameReadyStatus status)
    {
        // The wake test ends after the PC has slept, so say so even when the window is closed.
        var testFinished = _gameReady?.WakeTest.State is WakeTestStates.Countdown or WakeTestStates.Sleeping
            && status.WakeTest.State is WakeTestStates.Done or WakeTestStates.Failed;
        _gameReady = status;
        _displayWake.Enabled = status.PowerMode?.Mode == ConsoleIdleMode;
        _ambient.OnStatus(status);
        PushState();
        if (testFinished)
        {
            Notify(
                status.WakeTest.Outcome == WakeTestOutcomes.Controller ? "success" : "info",
                "Wake test",
                status.WakeTest.Message ?? "The wake test finished.");
        }
    }

    /// <summary>Failures and overdue backups are announced even with the host window closed, so the service is asked every five minutes.</summary>
    private const int GameSignalSeconds = 2;

    private void SendGameSignals()
    {
        if (!_connected)
        {
            return;
        }

        try
        {
            if (GameAware.ForegroundProbe.Read() is { } signals)
            {
                _ = _pipe.SendAsync(new GameSignalsMessage(signals));
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A window that vanished while it was read; the next tick tries again.
        }
    }

    private void PollBackup()
    {
        PollGameAware();
        if (_connected && (_backupTicks++ % BackupPollSeconds == 0 || (HostWindowOpen && _backupTicks % 5 == 0)))
        {
            _ = _pipe.SendAsync(new BackupStatusQueryMessage());
        }
    }

    /// <summary>Asked every ten seconds, window open or not, so a game that pauses VMs is announced.</summary>
    private void PollGameAware()
    {
        if (_connected && (_gameAwareTicks++ % 10 == 0 || (HostWindowOpen && _gameAwareTicks % 2 == 0)))
        {
            _ = _pipe.SendAsync(new GameAwareQueryMessage());
        }
    }

    private void OnGameAwareStatus(GameAwareInfo info)
    {
        var before = _gameAware?.State;
        _gameAware = info;
        if (before is { Gaming: false } && info.State.Gaming && info.State.ChangedVms.Count > 0)
        {
            Notify("info", "Game detected", $"{info.State.Process ?? "A game"} is running. {info.State.ChangedVms.Count} VM(s) paused or saved until it ends.");
        }
        else if (before is { Gaming: true } && !info.State.Gaming)
        {
            Notify("info", "Game ended", "Paused VMs were resumed.");
        }

        PushState();
    }

    private void OnBackupStatus(TrayBackupStatus status)
    {
        _backup = status;
        foreach (var (title, text) in _backupNotifier.Observe(status, DateTimeOffset.UtcNow))
        {
            Notify("error", title, text);
        }

        PushState();
    }

    private const int BackupPollSeconds = 300;

    private void PollUpdate()
    {
        PollBackup();
        if (!_connected || !HostWindowOpen)
        {
            return;
        }

        // Every second while something moves; every five seconds otherwise.
        var moving = _pendingCheck is not null || _update?.Activity is HostUpdateActivity.Checking or HostUpdateActivity.Preparing or HostUpdateActivity.Installing;
        var due = ++_pollTicks % 5 == 0;
        if (moving || due)
        {
            _ = _pipe.SendAsync(new UpdateStatusQueryMessage());
        }

        if (_hostLean?.Busy is not null || due)
        {
            _ = _pipe.SendAsync(new HostLeanQueryMessage());
        }

        // The service reads the PC again only when its last read is old, so this is cheap.
        if (due)
        {
            _ = _pipe.SendAsync(new GameReadyQueryMessage());
        }
    }

    private void QueryGpuVms()
    {
        if (_connected)
        {
            _ = _pipe.SendAsync(new GpuVmsQueryMessage());
        }
    }

    private void OnGpuVmsStopped(GpuVmsStoppedMessage message)
    {
        _gpuVms = message.StillRunning;
        if (!_shutdownGuard.Blocking)
        {
            return;
        }

        _shutdownGuard.Release();
        if (message.StillRunning.Count == 0)
        {
            _notifyIcon.ShowBalloonTip(10000, "GPU VMs are off", "You can restart or shut down Windows now.", ToolTipIcon.Info);
        }
        else
        {
            _notifyIcon.ShowBalloonTip(
                15000,
                "GPU VMs still running",
                $"{string.Join(", ", message.StillRunning)} did not shut down. Shut it down from the guest, or restart anyway and Hyper-V turns it off.",
                ToolTipIcon.Warning);
        }
    }

    private async Task ApproveWakeFixAsync(WakeFixRequestedMessage request)
    {
        // The tray and the service are the same executable (HyperHarbor.Host.exe).
        var result = await WakeFixApproval.HandleAsync(request, Environment.ProcessPath);
        await _pipe.SendAsync(result);

        var (title, kind) = result.Outcome switch
        {
            "applied" => ("Wake-on-LAN settings updated", "success"),
            "failed" => ("Wake-on-LAN settings not updated", "error"),
            _ => (string.Empty, string.Empty),
        };
        if (title.Length > 0)
        {
            Notify(kind, title, result.Detail ?? "Check the settings again from the client.");
        }
    }

    private void StartPairing(PairingStartedMessage started)
    {
        _pairing = started;
        if (!_webViewAvailable)
        {
            // Without WebView2 the PIN goes in a notification, so pairing still works.
            _notifyIcon.ShowBalloonTip(60_000, $"Pairing request from \"{started.DeviceName}\"", $"Enter {started.Pin[..3]} {started.Pin[3..]} on that device to pair it.", ToolTipIcon.Info);
            return;
        }

        if (_pinWindow is null)
        {
            var window = new WebWindow("HyperHarbor pairing", "pin", new Size(440, 400), new Size(400, 360))
            {
                TopMost = true,
                MaximizeBox = false,
                MinimizeBox = false,
            };
            window.PlaceOn(Screen.FromPoint(Cursor.Position));
            window.MessageReceived += OnPageMessage;
            window.FormClosed += (sender, _) =>
            {
                if (ReferenceEquals(sender, _pinWindow))
                {
                    _pinWindow = null;
                    if (_pairing is { } open)
                    {
                        // Closing the PIN window cancels the request, as Cancel does.
                        CancelPairing(open.PairingId);
                    }
                }
            };
            _pinWindow = window;
            _ = InitializeAsync(window);
        }

        PushState();
        _pinWindow.ShowWhenReady();
        _notifyIcon.ShowBalloonTip(5000, "Pairing request", $"\"{started.DeviceName}\" wants to pair with this PC.", ToolTipIcon.Info);
    }

    private void OnPairingEnded(PairingEndedMessage ended)
    {
        if (_pairing?.PairingId == ended.PairingId)
        {
            EndPairing();
        }

        var (title, text, kind) = ended.Outcome switch
        {
            "paired" => ("Device paired", $"\"{ended.DeviceName}\" can now connect to this PC.", "success"),
            "tooManyAttempts" => ("Pairing failed", $"Too many incorrect PINs from \"{ended.DeviceName}\".", "error"),
            "expired" => ("Pairing expired", $"The PIN for \"{ended.DeviceName}\" expired.", "info"),
            _ => (string.Empty, string.Empty, string.Empty),
        };

        if (title.Length > 0)
        {
            Notify(kind, title, text);
        }

        PushState();
    }

    private void EndPairing()
    {
        _pairing = null;
        var window = _pinWindow;
        _pinWindow = null;
        window?.Close();
    }

    // ITrayActions: what the page asks for.

    public void SetPassphrase(string passphrase)
    {
        if (!_connected)
        {
            Notify("error", "The passphrase was not saved", "The host service is not running.");
            PushState();
            return;
        }

        if (AdminPassphrase.Validate(passphrase) is { } problem)
        {
            Notify("error", "The passphrase was not saved", problem);
            PushState();
            return;
        }

        SetBusy("passphrase", true);

        // PBKDF2 with 600,000 iterations takes a moment; only the hash leaves this process.
        _ = Task.Run(() => AdminPassphrase.CreateHash(passphrase)).ContinueWith(
            hash => _ = _pipe.SendAsync(hash.Result),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void RemoveDevice(Guid deviceId)
    {
        SetBusy("device:" + deviceId, true);
        _ = _pipe.SendAsync(new RemoveDeviceMessage(deviceId));
    }

    public void ChooseFolder(TrayFolder folder)
    {
        var (key, description, current) = folder switch
        {
            TrayFolder.Vm => ("vm", "Choose where the host creates new VMs. Each VM gets its own folder here; existing VMs stay where they are.", _vmFolder is { IsDefault: false } chosen ? chosen.Folder : null),
            TrayFolder.Iso => ("iso", "Choose where the host stores the ISO images clients add. Images already in the current folder stay there.", _isoFolder),
            _ => ("backup", "Choose where the host puts VM disk exports. Each export gets its own folder here; earlier exports stay where they are.", _backupFolder),
        };

        using var dialog = new FolderBrowserDialog
        {
            Description = description,
            UseDescriptionForTitle = true,
            SelectedPath = current ?? string.Empty,
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog(_hostWindow) != DialogResult.OK || string.Equals(dialog.SelectedPath, current, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetBusy("folder:" + key, true);
        TrayMessage request = folder switch
        {
            TrayFolder.Vm => new SetVmFolderMessage(dialog.SelectedPath),
            TrayFolder.Iso => new SetIsoFolderMessage(dialog.SelectedPath),
            _ => new SetBackupFolderMessage(dialog.SelectedPath),
        };
        _ = _pipe.SendAsync(request);
    }

    public void SetUpConsole() =>
        _ = RunHelperAsync("console", "Console access", () => ConsoleAccessSetup.RunAsync(Environment.ProcessPath, DataDirectory));

    public void SetUpPackageSearch() =>
        _ = RunHelperAsync("packageSearch", "Package search", () => PackageSearchSetup.RunAsync(Environment.ProcessPath));

    /// <summary>Runs an elevated helper (the page asked first and shows it as in progress) and reports the result.</summary>
    private async Task RunHelperAsync(string key, string title, Func<Task<(bool Succeeded, string Message)>> run)
    {
        if (!_busy.Add(key))
        {
            return;
        }

        PushState();
        try
        {
            var (succeeded, message) = await run();
            Notify(succeeded ? "success" : "error", succeeded ? $"{title} is set up" : $"{title} was not set up", message);
        }
        finally
        {
            _busy.Remove(key);
            PushState();
        }
    }

    public void CheckForUpdate()
    {
        _pendingCheck = (_update?.LastCheck, DateTimeOffset.UtcNow);
        SetBusy("update", true);
        _ = _pipe.SendAsync(new CheckForUpdateMessage());
    }

    public void HostLeanDryRun(string source) => _ = _pipe.SendAsync(new HostLeanDryRunMessage(source));

    public void HostLeanApply(string source) => _ = _pipe.SendAsync(new HostLeanApplyMessage(source));

    public void SetHostLeanSchedule(bool enabled) => _ = _pipe.SendAsync(new SetHostLeanScheduleMessage(enabled));

    public void GameReadyRefresh() => _ = _pipe.SendAsync(new GameReadyQueryMessage(Force: true));

    public void GameReadyFix(string cardId) => _ = _pipe.SendAsync(new GameReadyFixMessage(cardId));

    public void GameReadyPowerModeApply(string mode, int displayMinutes, bool scheduleEnabled, string sleepTime, string wakeTime, double watts, double ratePerKwh) =>
        _ = _pipe.SendAsync(new GameReadyPowerModeApplyMessage(mode, displayMinutes, scheduleEnabled, sleepTime, wakeTime, watts, ratePerKwh));

    public void GameReadyAmbientApply(bool enabled, int idleMinutes, bool showHostStatus, bool showRunningVms, bool showDevices, bool softLock, string chord) =>
        _ = _pipe.SendAsync(new GameReadyAmbientApplyMessage(enabled, idleMinutes, showHostStatus, showRunningVms, showDevices, softLock, chord));

    public void GameReadySleepNow() => _ = _pipe.SendAsync(new GameReadySleepNowMessage());

    public void GameAwareApply(GameAwareSettings settings) => _ = _pipe.SendAsync(new GameAwareApplyMessage(settings));

    public void RestoreWizardAttach(RestoreWizardForm form) => _ = _pipe.SendAsync(new RestoreWizardAttachMessage(form));

    public void RestoreWizardRestore(string kind, string snapshotId, string? name, bool start) =>
        _ = _pipe.SendAsync(new RestoreWizardRestoreMessage(kind, snapshotId, name, start));

    public void RestoreWizardReset() => _ = _pipe.SendAsync(new RestoreWizardResetMessage());

    public void BackupRunNow() => _ = _pipe.SendAsync(new BackupRunNowMessage());

    public void GameAwareNotAGame() => _ = _pipe.SendAsync(new GameAwareNotAGameMessage());

    private void ConfirmSleepNow()
    {
        var answer = MessageBox.Show(
            "Put this PC to sleep? Remote sessions end, and a controller press or the network wakes it again if that is set up.",
            "Sleep now",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);
        if (answer == DialogResult.OK)
        {
            GameReadySleepNow();
        }
    }

    public void GameReadyTestWake(int delaySeconds) => _ = _pipe.SendAsync(new GameReadyTestWakeMessage(delaySeconds));

    public void GameReadyConsoleSignInEnable(string userName, string launcher) =>
        _ = _pipe.SendAsync(new GameReadyConsoleSignInEnableMessage(userName, launcher));

    public void GameReadyConsoleSignInDisable(bool deleteUser) =>
        _ = _pipe.SendAsync(new GameReadyConsoleSignInDisableMessage(deleteUser));

    public void InstallUpdate() => _ = _pipe.SendAsync(new InstallUpdateMessage());

    public void SetUpdateChannel(string channel) => _ = _pipe.SendAsync(new SetUpdateChannelMessage(channel));

    public void Open(string target)
    {
        var (path, folder) = target == "logs" ? (Path.Combine(DataDirectory, "logs"), true) : (Path.Combine(DataDirectory, "audit.log"), false);
        if (folder ? !Directory.Exists(path) : !File.Exists(path))
        {
            Notify("info", "Nothing to open yet", $"{path} does not exist yet. The host service creates it when it starts.");
            return;
        }

        try
        {
            // The log files grant access only to Administrators, SYSTEM, and the account running the service.
            Process.Start(new ProcessStartInfo(folder ? "explorer.exe" : "notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Notify("error", $"Could not open {Path.GetFileName(path)}", ex.Message);
        }
    }

    public void OpenUrl(string url)
    {
        if (WebWindow.IsExternalLinkAllowed(url))
        {
            Launch(url);
        }
    }

    public void CancelPairing(Guid pairingId)
    {
        if (_pairing?.PairingId != pairingId)
        {
            return;
        }

        _ = _pipe.SendAsync(new CancelPairingMessage());
        EndPairing();
        PushState();
    }

    public void CloseWindow() => _hostWindow?.Close();

    private static void Launch(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
