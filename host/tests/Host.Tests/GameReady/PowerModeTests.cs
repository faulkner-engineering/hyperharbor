using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Updates;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Host.Tray;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.GameReady;

internal sealed class FakePowerPlans : IPowerPlans
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    public Guid Active { get; set; } = Balanced;

    public Dictionary<Guid, PowerValues> Plans { get; } = new() { [Balanced] = new PowerValues(1800, 0, 600, 2, 120) };

    public List<string> Writes { get; } = [];

    /// <summary>The 1-based number of the write that throws; 0 for none.</summary>
    public int FailOnWrite { get; set; }

    public Guid ActivePlan() => Active;

    public PowerValues? Read(Guid plan) => Plans.GetValueOrDefault(plan);

    public void Write(Guid plan, PowerValues values)
    {
        Writes.Add($"{plan}:{values}");
        if (Writes.Count == FailOnWrite)
        {
            throw new InvalidOperationException("powercfg refused");
        }

        Plans[plan] = values;
    }
}

internal sealed class FakePowerSchedule : IPowerSchedule
{
    public (TimeOnly Sleep, TimeOnly Wake)? Registered { get; set; }

    public bool FailRegister { get; set; }

    public bool IsRegistered() => Registered is not null;

    public void Register(TimeOnly sleepAt, TimeOnly wakeAt)
    {
        if (FailRegister)
        {
            throw new InvalidOperationException("schtasks refused");
        }

        Registered = (sleepAt, wakeAt);
    }

    public void Remove() => Registered = null;
}

internal sealed class MemoryPowerModeStore : IPowerModeStore
{
    public PowerModeRecord? Record { get; set; }

    public PowerModeRecord? Load() => Record;

    public void Save(PowerModeRecord record) => Record = record;
}

public sealed class PowerModeTests
{
    private readonly FakePowerPlans _plans = new();
    private readonly FakePowerSchedule _schedule = new();
    private readonly MemoryPowerModeStore _store = new();

    private PowerModeService Create() => new(_plans, _schedule, _store, NullLogger<PowerModeService>.Instance);

    private static PowerModeSettings Settings(string mode, bool schedule = false, int display = 10) =>
        PowerModeSettings.Default with { Mode = mode, ScheduleEnabled = schedule, DisplayMinutes = display };

    private PowerValues Current => _plans.Plans[FakePowerPlans.Balanced];

    [Fact]
    public void ConsoleIdle_NeverSleeps_TurnsTheDisplayOffAfterTheChosenTime_AndKeepsTheRest()
    {
        Create().Apply(Settings(PowerModes.ConsoleIdle, display: 15));

        Assert.Equal(new PowerValues(0, 0, 900, 2, 0), Current);
        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), _store.Record!.Previous);
        Assert.Equal(FakePowerPlans.Balanced, _store.Record.PlanId);
        Assert.Single(_plans.Plans);
    }

    [Fact]
    public void Applying_IsIdempotent()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle));
        var writes = _plans.Writes.Count;

        service.Apply(Settings(PowerModes.ConsoleIdle));

        Assert.Equal(writes, _plans.Writes.Count);
        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), _store.Record!.Previous);
    }

    [Fact]
    public void Balanced_PutsTheEarlierValuesBack_AndForgetsThem()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle, schedule: true));

        service.Apply(Settings(PowerModes.Balanced));

        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), Current);
        Assert.Null(_store.Record!.Previous);
        Assert.Null(_schedule.Registered);
        Assert.True(service.Read().Applied);
    }

    [Fact]
    public void SwitchingBetweenModes_KeepsTheOriginalValuesForTheWayBack()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle));

        service.Apply(Settings(PowerModes.SleepWhenIdle));

        Assert.Equal(new PowerValues(900, 0, 300, 2, 120), Current);
        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), _store.Record!.Previous);

        service.Apply(Settings(PowerModes.Balanced));
        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), Current);
    }

    [Fact]
    public void ASchedule_AllowsWakeTimers_AndRegistersBothTasks()
    {
        Create().Apply(Settings(PowerModes.ConsoleIdle, schedule: true));

        Assert.Equal(1, Current.WakeTimers);
        Assert.Equal((new TimeOnly(1, 0), new TimeOnly(17, 0)), _schedule.Registered);
    }

    [Fact]
    public void AFailedWrite_PutsBackWhatTheCallChanged()
    {
        _plans.FailOnWrite = 1;

        var error = Assert.Throws<PowerModeException>(() => Create().Apply(Settings(PowerModes.ConsoleIdle)));

        Assert.Contains("powercfg refused", error.Message);
        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), Current);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void AFailedSchedule_RestoresThePlanValues()
    {
        _schedule.FailRegister = true;

        Assert.Throws<PowerModeException>(() => Create().Apply(Settings(PowerModes.ConsoleIdle, schedule: true)));

        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), Current);
        Assert.Null(_schedule.Registered);
        Assert.Null(_store.Record);
    }

    [Fact]
    public void WhenThePlanChanged_TheEarlierPlanIsRestoredBeforeTheNewOneIsChanged()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle));
        var other = Guid.NewGuid();
        _plans.Plans[other] = new PowerValues(600, 0, 300, 0, 120);
        _plans.Active = other;

        service.Apply(Settings(PowerModes.ConsoleIdle));

        Assert.Equal(new PowerValues(1800, 0, 600, 2, 120), Current);
        Assert.Equal(new PowerValues(0, 0, 600, 0, 0), _plans.Plans[other]);
        Assert.Equal(new PowerValues(600, 0, 300, 0, 120), _store.Record!.Previous);
        Assert.Equal(other, _store.Record.PlanId);
    }

    [Fact]
    public void Read_NoticesWhenThePlanDriftedFromTheMode()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle));
        _plans.Plans[FakePowerPlans.Balanced] = Current with { SleepSeconds = 1800 };

        var reading = service.Read();
        var card = GameReadyCards.PowerMode(reading, elevated: true);

        Assert.False(reading.Applied);
        Assert.Equal(GameReadyStates.Attention, card.State);
        Assert.Contains(card.Checks, check => check.Id == "sleepNever" && check.Status == GameReadyCheckStatuses.Fail);
    }

    [Fact]
    public void Read_ReportsAConsoleIdleModeAsReady()
    {
        var service = Create();
        service.Apply(Settings(PowerModes.ConsoleIdle, schedule: true));

        var card = GameReadyCards.PowerMode(service.Read(), elevated: true);

        Assert.Equal(GameReadyStates.Ready, card.State);
        Assert.All(card.Checks, check => Assert.Equal(GameReadyCheckStatuses.Pass, check.Status));
    }

    [Theory]
    [InlineData("loud", 10, "01:00", "17:00", 60, 0.15)]
    [InlineData(PowerModes.ConsoleIdle, 0, "01:00", "17:00", 60, 0.15)]
    [InlineData(PowerModes.ConsoleIdle, 10, "25:00", "17:00", 60, 0.15)]
    [InlineData(PowerModes.ConsoleIdle, 10, "01:00", "01:00", 60, 0.15)]
    [InlineData(PowerModes.ConsoleIdle, 10, "01:00", "17:00", 0, 0.15)]
    [InlineData(PowerModes.ConsoleIdle, 10, "01:00", "17:00", 60, -1)]
    public void InvalidSettings_AreRefusedBeforeAnythingChanges(string mode, int display, string sleep, string wake, double watts, double rate)
    {
        var settings = new PowerModeSettings(mode, display, true, sleep, wake, watts, rate);

        Assert.Throws<PowerModeException>(() => Create().Apply(settings));
        Assert.Empty(_plans.Writes);
    }

    [Fact]
    public void TheCostModel_ComparesAlwaysReadyWithTheSchedule()
    {
        var estimate = PowerCostModel.Estimate(PowerModeSettings.Default with { Watts = 60, RatePerKwh = 0.2 });

        Assert.Equal(43.8, estimate.AlwaysReadyKwh);
        Assert.Equal(8.76, estimate.AlwaysReadyMonthlyCost);
        Assert.True(estimate.ScheduledMonthlyCost < estimate.AlwaysReadyMonthlyCost);

        // 16 of 24 hours asleep at 3 W.
        var asleep = 16.0 / 24;
        var expected = Math.Round((60 * (1 - asleep) + 3 * asleep) * 730 / 1000, 1);
        Assert.Equal(expected, estimate.ScheduledKwh);
    }

    [Fact]
    public void TheGpuLoad_IsTheBusiestEngineTypeSummedOverProcesses()
    {
        var load = GpuLoad.Of(
        [
            new GpuEngineSample("pid_1_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 10),
            new GpuEngineSample("pid_2_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 5),
            new GpuEngineSample("pid_2_luid_0x0_0x1_phys_0_eng_3_engtype_Copy", 12),
            new GpuEngineSample("garbage", 99),
        ]);

        Assert.Equal(15, load);
        Assert.Null(GpuLoad.Of([]));
    }
}

public sealed class ScheduledSleepGateTests
{
    private static readonly PowerModeSettings Scheduled = PowerModeSettings.Default with { Mode = PowerModes.ConsoleIdle, ScheduleEnabled = true };
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 2, 0, 0, TimeSpan.Zero);

    private static ScheduledSleepGate.Inputs Inputs(
        PowerModeSettings? settings = null,
        int hour = 2,
        bool work = false,
        bool remote = false,
        TimeSpan? lastClientAgo = null,
        bool game = false) => new(
            settings ?? Scheduled,
            new DateTime(2026, 10, 11, hour, 0, 0),
            work,
            remote,
            lastClientAgo is { } ago ? Now - ago : null,
            Now,
            TimeSpan.FromMinutes(10),
            game);

    [Fact]
    public void WithNothingInUse_InsideTheSleepHours_TheHostMaySleep() => Assert.Null(ScheduledSleepGate.Block(Inputs()));

    [Theory]
    [InlineData(PowerModes.Balanced, true)]
    [InlineData(PowerModes.SleepWhenIdle, true)]
    [InlineData(PowerModes.ConsoleIdle, false)]
    public void WithoutConsoleIdleAndTheSchedule_NeverSleepsOnItsOwn(string mode, bool schedule)
    {
        var reason = ScheduledSleepGate.Block(Inputs(Scheduled with { Mode = mode, ScheduleEnabled = schedule }));

        Assert.Equal(ScheduledSleepGate.NotScheduled, reason);
        Assert.True(ScheduledSleepGate.IsFinal(reason!));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    [InlineData(23, false)]
    public void TheSleepHours_RunFromTheSleepTimeUpToTheWakeTime(int hour, bool inside)
    {
        var reason = ScheduledSleepGate.Block(Inputs(hour: hour));

        Assert.Equal(inside, reason is null);
        if (!inside)
        {
            Assert.Equal(ScheduledSleepGate.OutsideWindow, reason);
        }
    }

    [Fact]
    public void AWindowThatCrossesMidnight_IsHandled()
    {
        var overnight = Scheduled with { SleepTime = "23:00", WakeTime = "06:00" };

        Assert.True(ScheduledSleepGate.InWindow(overnight, new TimeOnly(23, 30)));
        Assert.True(ScheduledSleepGate.InWindow(overnight, new TimeOnly(5, 59)));
        Assert.False(ScheduledSleepGate.InWindow(overnight, new TimeOnly(6, 0)));
        Assert.False(ScheduledSleepGate.InWindow(overnight, new TimeOnly(12, 0)));
    }

    [Fact]
    public void WorkInProgress_ARemoteSession_AndRecentClientUse_KeepTheHostAwake()
    {
        Assert.NotNull(ScheduledSleepGate.Block(Inputs(work: true)));
        Assert.NotNull(ScheduledSleepGate.Block(Inputs(remote: true)));
        Assert.NotNull(ScheduledSleepGate.Block(Inputs(lastClientAgo: TimeSpan.FromMinutes(3))));
        Assert.Null(ScheduledSleepGate.Block(Inputs(lastClientAgo: TimeSpan.FromMinutes(30))));
        Assert.False(ScheduledSleepGate.IsFinal(ScheduledSleepGate.Block(Inputs(work: true))!));
    }

    [Fact]
    public void AGameInTheForeground_KeepsTheHostAwake()
    {
        Assert.Equal("A game is running in the foreground.", ScheduledSleepGate.Block(Inputs(game: true)));
    }
}

public sealed class ScheduledSleepServiceTests
{
    private sealed class CountingSleep : ISleepController
    {
        public int Count;

        public void Sleep() => Interlocked.Increment(ref Count);
    }

    private sealed class Sessions : IRemoteSessions
    {
        public bool Active { get; set; }

        public bool AnyActive() => Active;
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 2, 0, 0, TimeSpan.Zero));
    private readonly CountingSleep _sleep = new();
    private readonly Sessions _sessions = new();
    private readonly ForegroundGameTracker _game;
    private readonly MemoryPowerModeStore _store = new();

    public ScheduledSleepServiceTests()
    {
        _game = new ForegroundGameTracker(_time);
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _store.Record = new PowerModeRecord(PowerModeSettings.Default with { Mode = PowerModes.ConsoleIdle, ScheduleEnabled = true }, null, null);
    }

    private ScheduledSleepService Create()
    {
        var plans = new PowerModeService(new FakePowerPlans(), new FakePowerSchedule(), _store, NullLogger<PowerModeService>.Instance);
        return new ScheduledSleepService(
            plans,
            _sleep,
            new RemoteUseTracker(_time),
            new HostActivity(() => false, _time),
            _sessions,
            _game,
            new KeepAwakeOptions(),
            _time,
            NullLogger<ScheduledSleepService>.Instance);
    }

    [Fact]
    public async Task WhenNothingIsInUse_TheHostIsPutToSleepAfterTheDelay()
    {
        var (accepted, _, reason) = Create().Request();

        Assert.True(accepted);
        Assert.Null(reason);
        await Task.Delay(ScheduledSleepService.SleepDelay + TimeSpan.FromSeconds(2));
        Assert.Equal(1, _sleep.Count);
    }

    [Fact]
    public void ARemoteSession_OrAGame_BlocksItWithoutSleeping()
    {
        _sessions.Active = true;
        var (accepted, final, _) = Create().Request();
        Assert.False(accepted);
        Assert.False(final);

        _sessions.Active = false;
        _game.Report(true);
        Assert.False(Create().Request().Accepted);
        Assert.Equal(0, _sleep.Count);
    }

    [Fact]
    public void AGameReport_ExpiresWhenTheTrayStopsReporting()
    {
        _game.Report(true);
        Assert.True(_game.Active);

        _time.Advance(ForegroundGameTracker.MaximumAge + TimeSpan.FromSeconds(1));

        Assert.False(_game.Active);
    }
}

public sealed class PowerScheduleTasksTests
{
    [Fact]
    public void TheSleepTask_RunsTheHelperAtTheSleepTime_AndTriesAgainWhileTheHostIsInUse()
    {
        var xml = PowerScheduleTasks.SleepDefinition(new HyperHarbor.Host.Core.Installation.InstallLayout(@"C:\HH"), new TimeOnly(1, 0), new TimeOnly(17, 0)).ToString();

        Assert.Contains("T01:00:00", xml);
        Assert.Contains(@"C:\HH\hh-update.exe", xml);
        Assert.Contains("scheduled-sleep", xml);
        Assert.Contains("<Interval>PT15M</Interval>", xml);
        Assert.Contains("<Count>64</Count>", xml);
        Assert.Contains("<WakeToRun>false</WakeToRun>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains("S-1-5-18", xml);
    }

    [Fact]
    public void TheWakeTask_WakesTheComputer()
    {
        var xml = PowerScheduleTasks.WakeDefinition(new TimeOnly(17, 0)).ToString();

        Assert.Contains("T17:00:00", xml);
        Assert.Contains("<WakeToRun>true</WakeToRun>", xml);
    }
}

public sealed class DisplayWakeTests
{
    private sealed class Pads : IControllerInput
    {
        public long? State { get; set; } = 1;

        public int Polls { get; private set; }

        public long? Poll()
        {
            Polls++;
            return State;
        }
    }

    private sealed class Display : IDisplayControl
    {
        public int Forced { get; private set; }

        public void ForceOn() => Forced++;
    }

    private readonly Pads _pads = new();
    private readonly Display _display = new();
    private readonly FakeTimeProvider _time = new();

    private DisplayWake Create(bool enabled = true, bool displayOff = true) =>
        new(_pads, _display, _time) { Enabled = enabled, DisplayOff = displayOff };

    [Fact]
    public void AControllerChange_WhileTheDisplayIsOff_AsksForTheDisplayOnce()
    {
        var wake = Create();
        wake.Tick();

        _pads.State = 2;
        wake.Tick();
        wake.Tick();

        Assert.Equal(1, _display.Forced);
    }

    [Fact]
    public void WithoutAChange_TheDisplayIsLeftAlone()
    {
        var wake = Create();

        wake.Tick();
        wake.Tick();

        Assert.Equal(0, _display.Forced);
    }

    [Fact]
    public void WhileTheDisplayIsOn_ControllersAreNotEvenRead()
    {
        var wake = Create(displayOff: false);

        wake.Tick();
        _pads.State = 5;
        wake.Tick();

        Assert.Equal(0, _pads.Polls);
        Assert.Equal(0, _display.Forced);
    }

    [Fact]
    public void OutsideConsoleIdle_NothingHappens()
    {
        var wake = Create(enabled: false);

        wake.Tick();
        _pads.State = 5;
        wake.Tick();

        Assert.Equal(0, _display.Forced);
    }

    [Fact]
    public void TheDisplayGoingOffAgain_StartsFromTheStateAtThatMoment()
    {
        var wake = Create();
        wake.Tick();
        wake.DisplayOff = false;
        wake.Tick();

        // The pad moved while the display was on; that is not input to react to once it goes off.
        _pads.State = 9;
        wake.DisplayOff = true;
        wake.Tick();

        Assert.Equal(0, _display.Forced);
    }

    [Fact]
    public void NoControllerConnected_DoesNothing()
    {
        _pads.State = null;
        var wake = Create();

        wake.Tick();
        _pads.State = 3;
        wake.Tick();

        Assert.Equal(0, _display.Forced);
    }

    [Fact]
    public void AnotherChangeRightAfter_DoesNotAskAgain_ButALaterOneDoes()
    {
        var wake = Create();
        wake.Tick();
        _pads.State = 2;
        wake.Tick();

        _pads.State = 3;
        wake.Tick();
        Assert.Equal(1, _display.Forced);

        _time.Advance(DisplayWake.MinimumGap);
        _pads.State = 4;
        wake.Tick();
        Assert.Equal(2, _display.Forced);
    }
}

public sealed class ForegroundGameReporterTests
{
    [Theory]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(2, false)]
    [InlineData(5, false)]
    [InlineData(null, false)]
    public void OnlyAFullScreenDirect3DApplicationOrPresentationModeIsAGame(int? state, bool game)
    {
        bool? sent = null;

        new ForegroundGameReporter(() => state, active => sent = active).Tick();

        Assert.Equal(game, sent);
    }

    [Fact]
    public void ReportsChangesAtOnce_AndRepeatsAnUnchangedAnswerSoTheServiceKeepsHearingIt()
    {
        var sent = new List<bool>();
        var reporter = new ForegroundGameReporter(() => 3, sent.Add);

        for (var i = 0; i < ForegroundGameReporter.RepeatEvery * 2; i++)
        {
            reporter.Tick();
        }

        Assert.Equal(2, sent.Count);
    }
}

internal sealed class FakeIdleLoad : IIdleLoadReader
{
    public Task<IdleLoad> SampleAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new IdleLoad(3.5, null, DateTimeOffset.UnixEpoch));
}
