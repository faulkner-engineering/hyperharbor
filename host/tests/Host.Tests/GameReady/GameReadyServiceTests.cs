using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.GameReady.Ambient;
using HyperHarbor.Host.Core.GameReady.ConsoleSignIn;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Core.Wake;
using HyperHarbor.Host.Service.GameReady;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.GameReady;

public sealed class GameReadyServiceTests
{
    private const string Pad = "Xbox Wireless Controller";

    private sealed class FakePnp : IPnpDeviceReader
    {
        public List<PnpNode> Nodes { get; } = [new(Pad, @"USB\VID_045E&PID_02FD\1", "XboxComposite", null)];

        public Task<IReadOnlyList<PnpNode>> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PnpNode>>(Nodes.ToList());
    }

    private sealed class FakePower(FakeWakeEnvironment environment) : IPowerConfig
    {
        public string LastWake { get; set; } = "Wake History Count - 0";

        public bool? UsbSuspend { get; set; } = true;

        public List<string> Armed { get; } = [];

        public Task<string> LastWakeAsync(CancellationToken cancellationToken) => Task.FromResult(LastWake);

        public bool? UsbSelectiveSuspendOnAc() => UsbSuspend;

        public void EnableWake(string powercfgName)
        {
            Armed.Add(powercfgName);
            ((HashSet<string>)environment.Environment.WakeArmedDevices).Add(powercfgName);
        }

        public void DisableUsbSelectiveSuspendOnAc() => UsbSuspend = false;
    }

    private sealed class FakeFixes : INetworkWakeFixes
    {
        public List<string[]> Requests { get; } = [];

        public Task<bool> RequestAsync(IReadOnlyList<string> checkIds, CancellationToken cancellationToken)
        {
            Requests.Add(checkIds.ToArray());
            return Task.FromResult(true);
        }
    }

    private sealed class Sleeper(Action onSleep) : ISleepController
    {
        public void Sleep() => onSleep();
    }

    private readonly FakeWakeEnvironment _environment = new();
    private readonly FakePnp _pnp = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeFixes _fixes = new();
    private readonly FakeConsoleSignInSystem _consoleSystem = new();
    private FakePower? _power;

    private GameReadyService Create(bool elevated = true, ISleepController? sleep = null)
    {
        _power = new FakePower(_environment);
        var consoleSignIn = new ConsoleSignInService(_consoleSystem, new MemoryConsoleSignInStore(), NullLogger<ConsoleSignInService>.Instance);
        var powerMode = new PowerModeService(new FakePowerPlans(), new FakePowerSchedule(), new MemoryPowerModeStore(), NullLogger<PowerModeService>.Instance);
        return new GameReadyService(_environment, _pnp, _power, _fixes, sleep ?? new FakeSleepController(), consoleSignIn, powerMode, new FakeIdleLoad(), new AmbientService(new MemoryAmbientStore(), new FakeScreensaverFile(), NullLogger<AmbientService>.Instance), _time, NullLogger<GameReadyService>.Instance, elevated, TimeSpan.Zero, TimeSpan.Zero);
    }

    private void SetWake(bool armed, bool programmable)
    {
        var environment = _environment.Environment;
        _environment.Environment = environment with
        {
            WakeArmedDevices = armed ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pad } : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            WakeProgrammableDevices = programmable ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pad } : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static GameReadyCard Card(GameReadyService service, string id) => service.Status().Cards.Single(card => card.Id == id);

    [Fact]
    public async Task TheSection_HasTheFinalCards_InOrder_WithTheUnbuiltOnesComingSoon()
    {
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);

        var cards = service.Status().Cards;
        Assert.Equal(
            ["wakeOnLan", "controllerWake", "fastStartup", "usbSelectiveSuspend", "usbPowerInSleep", "consoleSignIn", "powerMode", "ambientScreen", "bootToLauncher", "steamDesktopLayout", "leanHost", "gameAwareMode"],
            cards.Select(card => card.Id));
        Assert.All(cards.Skip(8).Take(3), card => Assert.Equal(GameReadyStates.ComingSoon, card.State));
        Assert.All(cards.Skip(8), card => Assert.False(card.CanFix));
        Assert.Equal(GameReadyStates.Ready, cards[^1].State);
        Assert.Equal(GameReadyStates.Manual, Card(service, "usbPowerInSleep").State);
        Assert.DoesNotContain(Card(service, "wakeOnLan").Checks, check => check.Id == WakeCheckIds.FastStartupDisabled);
    }

    [Theory]
    [InlineData(true, true, GameReadyStates.Ready, ControllerWakeStates.Armed)]
    [InlineData(false, true, GameReadyStates.Attention, ControllerWakeStates.CanArm)]
    [InlineData(false, false, GameReadyStates.Attention, ControllerWakeStates.CannotWake)]
    public async Task TheControllerCard_ShowsTheThreeStates(bool armed, bool programmable, string state, string wake)
    {
        SetWake(armed, programmable);
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);

        var card = Card(service, "controllerWake");
        Assert.Equal(state, card.State);
        Assert.Equal(wake, Assert.Single(card.Controllers).Wake);
        Assert.Equal(wake == ControllerWakeStates.CanArm, card.CanFix);
        Assert.Equal(armed, card.CanTestWake);
    }

    [Fact]
    public async Task WithoutAdministratorRights_TheFixIsExplained_NotOffered()
    {
        SetWake(armed: false, programmable: true);
        var service = Create(elevated: false);
        await service.RefreshAsync(force: true, CancellationToken.None);

        var card = Card(service, "controllerWake");
        Assert.False(card.CanFix);
        Assert.Equal(GameReadyCards.NotElevatedReason, card.FixBlockedReason);

        await service.FixAsync("controllerWake", CancellationToken.None);
        Assert.Equal(GameReadyCards.NotElevatedReason, service.Status().Error);
        Assert.Empty(_power!.Armed);
    }

    [Fact]
    public async Task Fix_ArmsTheDevice_ThenReadsAgain()
    {
        SetWake(armed: false, programmable: true);
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);

        await service.FixAsync("controllerWake", CancellationToken.None);

        Assert.Equal([Pad], _power!.Armed);
        Assert.Equal(ControllerWakeStates.Armed, Assert.Single(Card(service, "controllerWake").Controllers).Wake);
        Assert.Null(service.Status().Error);
        Assert.Null(service.Status().Busy);
    }

    [Fact]
    public async Task UsbSelectiveSuspend_IsFixedByTurningItOff()
    {
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);
        Assert.True(Card(service, "usbSelectiveSuspend").CanFix);

        await service.FixAsync("usbSelectiveSuspend", CancellationToken.None);

        Assert.Equal(GameReadyStates.Ready, Card(service, "usbSelectiveSuspend").State);
    }

    [Fact]
    public async Task NetworkFixes_GoThroughTheExistingApprovalFlow_AndLeaveFastStartupToItsOwnCard()
    {
        _environment.Environment = WakeScenarios.ReadyDesktop() with
        {
            Power = WakeScenarios.ReadyDesktop().Power with { FastStartupEnabled = true },
        };
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);
        Assert.Equal(GameReadyStates.Attention, Card(service, "fastStartup").State);

        await service.FixAsync("fastStartup", CancellationToken.None);

        Assert.Equal([[WakeCheckIds.FastStartupDisabled]], _fixes.Requests);
    }

    [Fact]
    public async Task ACardWithoutAFix_IsRefused()
    {
        var service = Create();

        await Assert.ThrowsAsync<GameReadyException>(() => service.FixAsync("leanHost", CancellationToken.None));
    }

    [Fact]
    public async Task RecentReads_AreReused_UnlessForced()
    {
        var service = Create();
        await service.RefreshAsync(force: false, CancellationToken.None);
        _pnp.Nodes.Clear();

        await service.RefreshAsync(force: false, CancellationToken.None);
        Assert.Single(Card(service, "controllerWake").Controllers);

        _time.Advance(GameReadyService.MaximumAge + TimeSpan.FromSeconds(1));
        await service.RefreshAsync(force: false, CancellationToken.None);
        Assert.Empty(Card(service, "controllerWake").Controllers);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(301)]
    public async Task TheTestDelay_MustBeBetween5And300Seconds(int seconds)
    {
        SetWake(armed: true, programmable: true);
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);

        Assert.Throws<GameReadyException>(() => service.StartWakeTest(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public async Task TheTest_NeedsAnArmedDevice()
    {
        SetWake(armed: false, programmable: true);
        var service = Create();
        await service.RefreshAsync(force: true, CancellationToken.None);

        Assert.Throws<GameReadyException>(() => service.StartWakeTest(TimeSpan.FromSeconds(10)));
    }

    private async Task<GameReadyWakeTest> RunTestAsync(string lastWake)
    {
        SetWake(armed: true, programmable: true);
        var finished = new TaskCompletionSource<GameReadyWakeTest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleepCount = 0;
        var service = Create(sleep: new Sleeper(() => Interlocked.Increment(ref sleepCount)));
        _power!.LastWake = lastWake;
        service.Changed += (_, _) =>
        {
            var test = service.Status().WakeTest;
            if (test.State is WakeTestStates.Done or WakeTestStates.Failed)
            {
                finished.TrySetResult(test);
            }
        };
        await service.RefreshAsync(force: true, CancellationToken.None);

        var started = service.StartWakeTest(TimeSpan.FromSeconds(10));
        Assert.Equal(WakeTestStates.Countdown, started.State);
        Assert.Equal(_time.GetUtcNow() + TimeSpan.FromSeconds(10), started.SleepAt);
        Assert.Throws<GameReadyException>(() => service.StartWakeTest(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, sleepCount);

        _time.Advance(TimeSpan.FromSeconds(10));
        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, sleepCount);
        return result;
    }

    [Fact]
    public async Task AControllerWake_IsReportedAsSuccess()
    {
        var result = await RunTestAsync("""
            Wake History Count - 1
            Wake History [0]
              Wake Source [0]
                Type: Device
                Instance Path: USB\VID_045E&PID_02FD\1
                Friendly Name: Xbox Wireless Controller
            """);

        Assert.Equal(WakeTestStates.Done, result.State);
        Assert.Equal(WakeTestOutcomes.Controller, result.Outcome);
        Assert.Contains("Waking from the controller works", result.Message);
    }

    [Fact]
    public async Task AWakeByThePowerButton_IsNotAControllerWake()
    {
        var result = await RunTestAsync("""
            Wake History Count - 1
            Wake History [0]
              Wake Source [0]
                Type: Fixed Feature
                Fixed Feature Type: Power Button
            """);

        Assert.Equal(WakeTestOutcomes.OtherSource, result.Outcome);
        Assert.Equal("Power Button", result.Source);
    }

    [Fact]
    public async Task AnEmptyWakeHistory_IsReportedHonestly()
    {
        var result = await RunTestAsync("Wake History Count - 0");

        Assert.Equal(WakeTestOutcomes.NoSource, result.Outcome);
    }

    [Fact]
    public async Task APCThatCannotSleep_FailsTheTest()
    {
        SetWake(armed: true, programmable: true);
        var finished = new TaskCompletionSource<GameReadyWakeTest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = Create(sleep: new Sleeper(() => throw new System.ComponentModel.Win32Exception("The computer could not be put to sleep.")));
        service.Changed += (_, _) =>
        {
            if (service.Status().WakeTest.State == WakeTestStates.Failed)
            {
                finished.TrySetResult(service.Status().WakeTest);
            }
        };
        await service.RefreshAsync(force: true, CancellationToken.None);

        service.StartWakeTest(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromSeconds(5));

        var result = await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("could not be put to sleep", result.Message);
    }
}
