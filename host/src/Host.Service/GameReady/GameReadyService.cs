using HyperHarbor.Host.Core.GameReady;
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
    private GameReadyWakeTest _wakeTest = new(WakeTestStates.Idle, null, null, null, null, null);
    private ITimer? _testTimer;

    public GameReadyService(
        IWakeEnvironmentReader environment,
        IPnpDeviceReader devices,
        IPowerConfig power,
        INetworkWakeFixes networkFixes,
        ISleepController sleep,
        TimeProvider time,
        ILogger<GameReadyService> logger)
        : this(environment, devices, power, networkFixes, sleep, time, logger, Environment.IsPrivilegedProcess, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5))
    {
    }

    internal GameReadyService(
        IWakeEnvironmentReader environment,
        IPnpDeviceReader devices,
        IPowerConfig power,
        INetworkWakeFixes networkFixes,
        ISleepController sleep,
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
            return new GameReadyStatus(_readAt, _busy, _error, _cards, _wakeTest);
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

        var cards = new List<GameReadyCard>
        {
            GameReadyCards.WakeOnLan(WakeReadinessEvaluator.Evaluate(environment)),
            GameReadyCards.WakeFromController(controllers, _elevated),
            GameReadyCards.FastStartup(environment.Power),
            GameReadyCards.UsbSelectiveSuspend(_power.UsbSelectiveSuspendOnAc(), _elevated),
            GameReadyCards.UsbPowerInSleep(),
        };
        cards.AddRange(GameReadyCards.ComingSoon());

        lock (_gate)
        {
            _controllers = controllers;
            _cards = cards;
            _readAt = _time.GetUtcNow();
        }

        Changed?.Invoke(this, EventArgs.Empty);
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
