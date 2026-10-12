using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;
using HyperHarbor.Host.Core.GameReady.Ambient;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Wake;

namespace HyperHarbor.Host.Service.GameReady;

/// <summary>An action the Game ready section refused. The message is shown to the person at the host.</summary>
public sealed class GameReadyException(string message) : Exception(message);

/// <summary>Asks for the Wake-on-LAN and fast startup fixes, which already have an approval flow when the service is not elevated.</summary>
public interface INetworkWakeFixes
{
    /// <returns>True when the fixes were applied; false when the user at the host is asked to approve them.</returns>
    Task<bool> RequestAsync(IReadOnlyList<string> checkIds, CancellationToken cancellationToken);
}

public sealed class WakeFixCoordinatorFixes(Wake.WakeFixCoordinator coordinator) : INetworkWakeFixes
{
    public async Task<bool> RequestAsync(IReadOnlyList<string> checkIds, CancellationToken cancellationToken)
    {
        try
        {
            var (outcome, _) = await coordinator.RequestAsync(checkIds, "this PC", cancellationToken);
            return outcome == Wake.WakeFixOutcome.Applied;
        }
        catch (Exception ex) when (ex is InvalidWakeRequestException or Wake.WakeFixUnavailableException)
        {
            throw new GameReadyException(ex.Message);
        }
    }
}

/// <summary>
/// Reads the PC for the Game ready section, applies the Fix of a card, and runs the Test wake action. The tray shows
/// <see cref="Status"/> and hears <see cref="Changed"/>. Reads are cached; nothing changes without a request.
/// </summary>
public sealed class GameReadyService
{
    /// <summary>A query from the tray reads again only when the last read is older than this.</summary>
    public static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan MinimumTestDelay = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaximumTestDelay = TimeSpan.FromSeconds(300);

    private readonly IWakeEnvironmentReader _environment;
    private readonly IPnpDeviceReader _devices;
    private readonly IPowerConfig _power;
    private readonly INetworkWakeFixes _networkFixes;
    private readonly ISleepController _sleep;
    private readonly ConsoleSignInService _consoleSignIn;
    private readonly PowerModeService _powerMode;
    private readonly IIdleLoadReader _idleLoad;
    private readonly AmbientService _ambient;
    private readonly TimeProvider _time;
    private readonly ILogger<GameReadyService> _logger;
    private readonly bool _elevated;
    private readonly TimeSpan _resumeSettle;
    private readonly TimeSpan _beforeSleep;
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private readonly object _gate = new();

    private IReadOnlyList<GameReadyCard> _cards = [];
    private IReadOnlyList<GameController> _controllers = [];
    private DateTimeOffset? _readAt;
    private string? _busy;
    private string? _error;
    private ConsoleSignInInfo? _consoleSignInInfo;
    private PowerModeInfo? _powerModeInfo;
    private AmbientInfo? _ambientInfo;
    private AmbientReportMessage? _ambientReport;
    private CecDevice? _cec;
    private GameReadyWakeTest _wakeTest = new(WakeTestStates.Idle, null, null, null, null, null);
    private ITimer? _testTimer;

    public GameReadyService(
        IWakeEnvironmentReader environment,
        IPnpDeviceReader devices,
        IPowerConfig power,
        INetworkWakeFixes networkFixes,
        ISleepController sleep,
        ConsoleSignInService consoleSignIn,
        PowerModeService powerMode,
        IIdleLoadReader idleLoad,
        AmbientService ambient,
        TimeProvider time,
        ILogger<GameReadyService> logger)
        : this(environment, devices, power, networkFixes, sleep, consoleSignIn, powerMode, idleLoad, ambient, time, logger, Environment.IsPrivilegedProcess, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
    {
    }

    internal GameReadyService(
        IWakeEnvironmentReader environment,
        IPnpDeviceReader devices,
        IPowerConfig power,
        INetworkWakeFixes networkFixes,
        ISleepController sleep,
        ConsoleSignInService consoleSignIn,
        PowerModeService powerMode,
        IIdleLoadReader idleLoad,
        AmbientService ambient,
        TimeProvider time,
        ILogger<GameReadyService> logger,
        bool elevated,
        TimeSpan beforeSleep,
        TimeSpan resumeSettle)
    {
        _beforeSleep = beforeSleep;
        _environment = environment;
        _devices = devices;
        _power = power;
        _networkFixes = networkFixes;
        _sleep = sleep;
        _consoleSignIn = consoleSignIn;
        _powerMode = powerMode;
        _idleLoad = idleLoad;
        _ambient = ambient;
        _time = time;
        _logger = logger;
        _elevated = elevated;
        _resumeSettle = resumeSettle;
    }

    /// <summary>Raised after every change of <see cref="Status"/>.</summary>
    public event EventHandler? Changed;

    public GameReadyStatus Status()
    {
        lock (_gate)
        {
            return new GameReadyStatus(_readAt, _busy, _error, _cards, _wakeTest, _consoleSignInInfo, _powerModeInfo, _ambientInfo);
        }
    }

    /// <summary>Reads the PC when the last read is older than <see cref="MaximumAge"/>, or always when forced.</summary>
    public async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        if (!force && _readAt is { } at && _time.GetUtcNow() - at < MaximumAge)
        {
            return;
        }

        if (!await _readLock.WaitAsync(0, cancellationToken))
        {
            return; // A read is already running; it raises Changed when it finishes.
        }

        try
        {
            SetBusy(GameReadyBusy.Reading);
            try
            {
                await ReadAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or Microsoft.Management.Infrastructure.CimException or InvalidOperationException or IOException)
            {
                _logger.LogWarning(ex, "Reading the Game ready state failed.");
                SetError($"Reading this PC failed: {ex.Message}");
            }
            finally
            {
                SetBusy(null);
            }
        }
        finally
        {
            _readLock.Release();
        }
    }

    /// <summary>Applies the Fix of one card and reads the PC again.</summary>
    /// <exception cref="GameReadyException">The card has no fix, or nothing can be fixed now.</exception>
    public async Task FixAsync(string cardId, CancellationToken cancellationToken)
    {
        if (!GameReadyCardIds.Fixable.Contains(cardId))
        {
            throw new GameReadyException("This card has no Fix action.");
        }

        lock (_gate)
        {
            if (_busy is not null && _busy != GameReadyBusy.Reading)
            {
                throw new GameReadyException("Another action is still running.");
            }

            _busy = GameReadyBusy.FixPrefix + cardId;
            _error = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await ApplyFixAsync(cardId, cancellationToken);
            await _readLock.WaitAsync(cancellationToken);
            try
            {
                await ReadAsync(cancellationToken);
            }
            finally
            {
                _readLock.Release();
            }
        }
        catch (GameReadyException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The Game ready fix {CardId} failed.", cardId);
            SetError($"The fix failed: {ex.Message}");
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>Starts the Test wake countdown. The PC sleeps at its end; the result follows the wake.</summary>
    /// <exception cref="GameReadyException">The delay is out of range, a test is running, or no controller is armed.</exception>
    public GameReadyWakeTest StartWakeTest(TimeSpan delay)
    {
        if (delay < MinimumTestDelay || delay > MaximumTestDelay)
        {
            throw new GameReadyException("The delay must be between 5 and 300 seconds.");
        }

        lock (_gate)
        {
            if (_wakeTest.State is WakeTestStates.Countdown or WakeTestStates.Sleeping)
            {
                throw new GameReadyException("A wake test is already running.");
            }

            if (!_controllers.Any(controller => controller.Wake == ControllerWake.Armed))
            {
                throw new GameReadyException("No controller or receiver is armed to wake the PC. Arm one first.");
            }

            var sleepAt = _time.GetUtcNow() + delay;
            _error = null;
            _wakeTest = new GameReadyWakeTest(WakeTestStates.Countdown, sleepAt, null, null, null, null);
            _testTimer = _time.CreateTimer(_ => _ = RunWakeTestAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }

        _logger.LogInformation("Controller wake test: the PC sleeps in {Delay}.", delay);
        Changed?.Invoke(this, EventArgs.Empty);
        return Status().WakeTest;
    }

    private async Task ApplyFixAsync(string cardId, CancellationToken cancellationToken)
    {
        switch (cardId)
        {
            case GameReadyCardIds.WakeOnLan:
                var ids = WakeReadinessEvaluator.Evaluate(await _environment.ReadAsync(cancellationToken)).Checks
                    .Where(check => check.AutoFixAvailable && check.Id != WakeCheckIds.FastStartupDisabled)
                    .Select(check => check.Id)
                    .ToList();
                if (ids.Count == 0)
                {
                    throw new GameReadyException("Nothing can be fixed automatically.");
                }

                await FixNetworkAsync(ids, cancellationToken);
                break;
            case GameReadyCardIds.FastStartup:
                await FixNetworkAsync([WakeCheckIds.FastStartupDisabled], cancellationToken);
                break;
            case GameReadyCardIds.ControllerWake:
                RequireElevation();
                var targets = _controllers.SelectMany(controller => controller.ArmTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (targets.Count == 0)
                {
                    throw new GameReadyException("Every device that can wake the PC is already armed.");
                }

                foreach (var target in targets)
                {
                    _power.EnableWake(target);
                    _logger.LogInformation("Armed {Device} to wake the PC.", target);
                }

                break;
            case GameReadyCardIds.UsbSelectiveSuspend:
                RequireElevation();
                _power.DisableUsbSelectiveSuspendOnAc();
                _logger.LogInformation("Turned off USB selective suspend on AC power.");
                break;
        }
    }

    private async Task FixNetworkAsync(IReadOnlyList<string> checkIds, CancellationToken cancellationToken)
    {
        // Unelevated, the tray asks the user to approve; the answer arrives later and the next read shows the result.
        if (!await _networkFixes.RequestAsync(checkIds, cancellationToken))
        {
            SetError("Approve the administrator prompt on this PC, then check again.");
        }
    }

    private void RequireElevation()
    {
        if (!_elevated)
        {
            throw new GameReadyException(GameReadyCards.NotElevatedReason);
        }
    }

    private async Task ReadAsync(CancellationToken cancellationToken)
    {
        var environment = await _environment.ReadAsync(cancellationToken);
        var nodes = await _devices.ReadAsync(cancellationToken);
        var controllers = ControllerWakeEvaluator.Build(nodes, environment.WakeArmedDevices, environment.WakeProgrammableDevices);

        var consoleSignIn = ReadConsoleSignIn();
        var powerMode = await ReadPowerModeAsync(cancellationToken);
        var cec = CecDetector.Find(nodes);
        var ambient = _ambient.Read(cec);
        AmbientReportMessage? report;
        lock (_gate)
        {
            report = _ambientReport;
        }
        var cards = new List<GameReadyCard>
        {
            GameReadyCards.WakeOnLan(WakeReadinessEvaluator.Evaluate(environment)),
            GameReadyCards.WakeFromController(controllers, _elevated),
            GameReadyCards.FastStartup(environment.Power),
            GameReadyCards.UsbSelectiveSuspend(_power.UsbSelectiveSuspendOnAc(), _elevated),
            GameReadyCards.UsbPowerInSleep(),
            GameReadyCards.ConsoleSignIn(consoleSignIn, _elevated),
            GameReadyCards.PowerMode(powerMode.Reading, _elevated),
            GameReadyCards.AmbientScreen(ambient, powerMode.Reading.Settings, report?.Screensaver, _elevated),
        };
        cards.AddRange(GameReadyCards.ComingSoon());

        lock (_gate)
        {
            _controllers = controllers;
            _cards = cards;
            _consoleSignInInfo = new ConsoleSignInInfo(
                consoleSignIn.UserName,
                consoleSignIn.Launcher,
                consoleSignIn.Configured,
                consoleSignIn.Launchers.Select(launcher => new ConsoleSignInLauncher(launcher.Id, launcher.Name, launcher.Available, launcher.Reason)).ToList());
            _powerModeInfo = powerMode.Info;
            _cec = cec;
            _ambientInfo = BuildAmbientInfo(ambient, report);
            _readAt = _time.GetUtcNow();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AmbientInfo BuildAmbientInfo(AmbientReading reading, AmbientReportMessage? report)
    {
        var settings = reading.Settings;
        return new AmbientInfo(
            settings.Enabled,
            settings.IdleMinutes,
            settings.ShowHostStatus,
            settings.ShowRunningVms,
            settings.ShowDevices,
            settings.SoftLock,
            settings.Chord,
            reading.Supported,
            reading.UnsupportedReason,
            reading.ScreensaverPath,
            report?.Screensaver,
            report?.CpuPercent,
            report?.GpuPercent,
            report?.MeasuredSeconds,
            reading.CecDevice);
    }

    /// <summary>The saved power mode, how the active plan compares, the cost model, and (in Console idle) the measured load.</summary>
    private async Task<(PowerModeReading Reading, PowerModeInfo Info)> ReadPowerModeAsync(CancellationToken cancellationToken)
    {
        var reading = _powerMode.Read();
        var settings = reading.Settings;
        IdleLoad? load = null;
        if (settings.Mode == PowerModes.ConsoleIdle)
        {
            try
            {
                load = await _idleLoad.SampleAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or Microsoft.Management.Infrastructure.CimException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "The idle load could not be measured.");
            }
        }

        var estimate = PowerCostModel.Estimate(settings);
        return (
            reading,
            new PowerModeInfo(
                settings.Mode,
                settings.DisplayMinutes,
                settings.ScheduleEnabled,
                settings.SleepTime,
                settings.WakeTime,
                settings.Watts,
                settings.RatePerKwh,
                reading.Applied && reading.ScheduleInPlace,
                new PowerModeEstimate(estimate.Watts, estimate.AlwaysReadyKwh, estimate.AlwaysReadyMonthlyCost, estimate.ScheduledKwh, estimate.ScheduledMonthlyCost, PowerCostModel.SleepWatts),
                load?.CpuPercent,
                load?.GpuPercent));
    }

    /// <summary>Applies a power mode and reads the PC again. Needs administrator rights.</summary>
    /// <exception cref="GameReadyException">The settings are not valid or a step failed; the message says why.</exception>
    public async Task ApplyPowerModeAsync(PowerModeSettings settings, CancellationToken cancellationToken)
    {
        RequireElevation();
        lock (_gate)
        {
            if (_busy is not null && _busy != GameReadyBusy.Reading)
            {
                throw new GameReadyException("Another action is still running.");
            }

            _busy = GameReadyBusy.PowerMode;
            _error = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            // The ambient screen must start before the display turns off, so a shorter display timeout is refused.
            if (_ambient.DisplayTimeoutProblem(settings) is { } ordering)
            {
                SetError(ordering);
            }
            else
            {
                await Task.Run(() => _powerMode.Apply(settings), cancellationToken);
            }
        }
        catch (PowerModeException ex)
        {
            SetError(ex.Message);
        }

        try
        {
            await _readLock.WaitAsync(cancellationToken);
            try
            {
                await ReadAsync(cancellationToken);
            }
            finally
            {
                _readLock.Release();
            }
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>Saves the Ambient screen choices and reads the PC again. Needs administrator rights (the screensaver file is in Program Files).</summary>
    /// <exception cref="GameReadyException">Another action is running or the host is not elevated.</exception>
    public async Task ApplyAmbientAsync(AmbientSettings settings, CancellationToken cancellationToken)
    {
        RequireElevation();
        lock (_gate)
        {
            if (_busy is not null && _busy != GameReadyBusy.Reading)
            {
                throw new GameReadyException("Another action is still running.");
            }

            _busy = GameReadyBusy.Ambient;
            _error = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.Run(() => _ambient.Apply(settings, _powerMode.Settings), cancellationToken);
        }
        catch (AmbientException ex)
        {
            SetError(ex.Message);
        }

        try
        {
            await _readLock.WaitAsync(cancellationToken);
            try
            {
                await ReadAsync(cancellationToken);
            }
            finally
            {
                _readLock.Release();
            }
        }
        finally
        {
            SetBusy(null);
        }
    }

    /// <summary>
    /// The tray's report of the desktop screensaver and the screen's measured cost. The card's checks read it, so the PC
    /// is read again.
    /// </summary>
    public void ReportAmbient(AmbientReportMessage report)
    {
        ArgumentNullException.ThrowIfNull(report);
        bool changed;
        lock (_gate)
        {
            changed = _ambientReport is null || _ambientReport.Screensaver != report.Screensaver;
            _ambientReport = report;
            if (_ambientInfo is { } info)
            {
                _ambientInfo = info with
                {
                    Screensaver = report.Screensaver,
                    CpuPercent = report.CpuPercent,
                    GpuPercent = report.GpuPercent,
                    MeasuredSeconds = report.MeasuredSeconds,
                };
            }
        }

        if (changed)
        {
            _ = RefreshAsync(force: true, CancellationToken.None);
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Puts the host to sleep shortly, so the tray can show that it is going to.</summary>
    public void SleepNow()
    {
        _logger.LogInformation("Sleep now was requested at the host.");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_beforeSleep);
                _sleep.Sleep();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Sleep now failed.");
                SetError($"The PC could not be put to sleep: {ex.Message}");
            }
        });
    }

    /// <summary>The Console user and launcher are kept between reads so the form does not forget what was typed.</summary>
    private ConsoleSignInReading ReadConsoleSignIn()
    {
        var userName = _consoleSignInInfo?.UserName;
        var launcher = _consoleSignInInfo?.Launcher;
        try
        {
            return _consoleSignIn.Read(userName, launcher);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException or System.Security.Cryptography.CryptographicException)
        {
            // Without administrator rights the account, the LSA secret, and the saved record cannot be read.
            _logger.LogDebug(ex, "The Console sign-in could not be read.");
            return new ConsoleSignInReading(
                userName ?? ConsoleUserPolicy.DefaultUserName,
                launcher ?? ConsoleLaunchers.SteamBigPicture,
                Configured: false,
                UserExists: false,
                UserIsStandard: false,
                AutoSignInSet: false,
                LauncherSet: false,
                PlaintextPasswordPresent: false,
                AdminAccountReport.None,
                []);
        }
    }

    /// <summary>Sets up the Console sign-in and reads the PC again. A failed setup undoes its own changes.</summary>
    /// <exception cref="GameReadyException">The sign-in cannot be set up; the message says why.</exception>
    public Task EnableConsoleSignInAsync(string userName, string launcher, CancellationToken cancellationToken) =>
        RunConsoleSignInAsync(
            () => _consoleSignIn.Enable(userName, launcher),
            () => _consoleSignInInfo = new ConsoleSignInInfo(userName.Trim(), launcher, false, _consoleSignInInfo?.Launchers ?? []),
            cancellationToken);

    /// <summary>Turns the Console sign-in off and reads the PC again.</summary>
    public Task DisableConsoleSignInAsync(bool deleteUser, CancellationToken cancellationToken) =>
        RunConsoleSignInAsync(() => _consoleSignIn.Disable(deleteUser), null, cancellationToken);

    private async Task RunConsoleSignInAsync(Action action, Action? remember, CancellationToken cancellationToken)
    {
        RequireElevation();
        lock (_gate)
        {
            if (_busy is not null && _busy != GameReadyBusy.Reading)
            {
                throw new GameReadyException("Another action is still running.");
            }

            _busy = GameReadyBusy.ConsoleSignIn;
            _error = null;
            remember?.Invoke();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await Task.Run(action, cancellationToken);
        }
        catch (ConsoleSignInException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The Console sign-in action failed.");
            SetError($"The Console sign-in action failed: {ex.Message}");
        }

        try
        {
            await _readLock.WaitAsync(cancellationToken);
            try
            {
                await ReadAsync(cancellationToken);
            }
            finally
            {
                _readLock.Release();
            }
        }
        finally
        {
            SetBusy(null);
        }
    }

    private async Task RunWakeTestAsync()
    {
        lock (_gate)
        {
            _testTimer?.Dispose();
            _testTimer = null;
            _wakeTest = _wakeTest with { State = WakeTestStates.Sleeping };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            // Let the tray hear that the PC is about to sleep.
            await Task.Delay(_beforeSleep);
            _logger.LogInformation("Controller wake test: going to sleep.");
            await Task.Run(_sleep.Sleep);
            var resumedAt = _time.GetUtcNow();
            _logger.LogInformation("Controller wake test: the PC resumed.");

            // Windows fills in the wake history a moment after the resume.
            await Task.Delay(_resumeSettle);
            var result = LastWakeParser.Parse(await _power.LastWakeAsync(CancellationToken.None));
            IReadOnlyList<GameController> controllers;
            lock (_gate)
            {
                controllers = _controllers;
            }

            Finish(resumedAt, WakeAttributor.Attribute(result, controllers));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning(ex, "The controller wake test failed.");
            lock (_gate)
            {
                _wakeTest = new GameReadyWakeTest(WakeTestStates.Failed, null, null, null, null, $"The test could not finish: {ex.Message}");
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            await RefreshAsync(force: true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Reading the PC after the wake test failed.");
        }
    }

    private void Finish(DateTimeOffset resumedAt, WakeAttribution attribution)
    {
        var (outcome, message) = Describe(attribution);
        lock (_gate)
        {
            _wakeTest = new GameReadyWakeTest(WakeTestStates.Done, null, resumedAt, outcome, attribution.SourceName, message);
        }

        _logger.LogInformation("Controller wake test result: {Outcome} ({Source}).", outcome, attribution.SourceName ?? "none");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What a result means for the person at the PC.</summary>
    internal static (string Outcome, string Message) Describe(WakeAttribution attribution) => attribution.Kind switch
    {
        WakeAttributionKind.Controller => (
            WakeTestOutcomes.Controller,
            $"{attribution.Controller!.Name} woke the PC. Waking from the controller works."),
        WakeAttributionKind.OtherDevice => (
            WakeTestOutcomes.OtherDevice,
            $"Windows says {attribution.SourceName} woke the PC, not a controller. If you pressed a controller button, arm its receiver or try another USB port, then test again."),
        WakeAttributionKind.OtherSource => (
            WakeTestOutcomes.OtherSource,
            $"Windows says the wake came from {attribution.SourceName}, not a controller. A power button press or a wake timer does this."),
        _ => (
            WakeTestOutcomes.NoSource,
            "Windows did not record what woke the PC. Press a controller button after the PC sleeps, and check the BIOS USB power setting if it stays asleep."),
    };

    /// <summary>Shows a refusal in the status, for the person at the host.</summary>
    public void ReportError(string message) => SetError(message);

    private void SetBusy(string? busy)
    {
        lock (_gate)
        {
            _busy = busy;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _error = message;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
