using HyperHarbor.Host.Core.GameReady;
using HyperHarbor.Host.Core.GameReady.Ambient;
using HyperHarbor.Host.Core.GameReady.PowerMode;
using HyperHarbor.Host.Service.GameReady;
using HyperHarbor.Host.Service.Installation;
using HyperHarbor.Host.Tray.Ambient;
using HyperHarbor.Shared.Contracts.Ipc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.GameReady;

internal sealed class MemoryAmbientStore : IAmbientStore
{
    public AmbientSettings? Settings { get; set; }

    public AmbientSettings? Load() => Settings;

    public void Save(AmbientSettings settings) => Settings = settings;
}

internal sealed class FakeScreensaverFile(string? unsupported = null) : IAmbientScreensaverFile
{
    public const string Path = @"C:\Program Files\HyperHarbor\HyperHarbor Ambient.scr";

    public int Installs { get; private set; }

    public string Ensure()
    {
        if (unsupported is not null)
        {
            throw new AmbientException(unsupported);
        }

        Installs++;
        return Path;
    }

    public string? CurrentPath() => unsupported is null && Installs > 0 ? Path : null;

    public string? UnsupportedReason() => unsupported;
}

public sealed class AmbientChordTests
{
    private const ushort Lb = 0x0100;
    private const ushort Rb = 0x0200;
    private const ushort Start = 0x0010;
    private const ushort A = 0x1000;

    [Theory]
    [InlineData("lb+rb+start", true)]
    [InlineData("LB + RB + Start", true)]
    [InlineData("back+start", true)]
    [InlineData("a", false)]
    [InlineData("lb+lb", false)]
    [InlineData("lb+nope", false)]
    [InlineData("a+b+x+y+lb", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Parse_AcceptsTwoToFourKnownDistinctButtons(string? text, bool valid) =>
        Assert.Equal(valid, AmbientChord.Parse(text) is not null);

    [Fact]
    public void Normalize_UsesAFixedOrderAndLowerCase() =>
        Assert.Equal("lb+rb+start", AmbientChord.Normalize("RB + Start + LB"));

    [Fact]
    public void Describe_NamesTheButtonsForAPerson() =>
        Assert.Equal("LB + RB + Start", AmbientChord.Describe("start+rb+lb"));

    [Fact]
    public void IsHeld_NeedsEveryButtonOnOnePad()
    {
        var chord = AmbientChord.Parse("lb+rb+start")!.Value;
        Assert.Equal(Lb | Rb | Start, chord);
        Assert.True(AmbientChord.IsHeld([(ushort)(Lb | Rb | Start | A)], chord));
        Assert.False(AmbientChord.IsHeld([(ushort)(Lb | Rb)], chord));
        Assert.False(AmbientChord.IsHeld([Lb, (ushort)(Rb | Start)], chord));
        Assert.False(AmbientChord.IsHeld([], chord));
        Assert.False(AmbientChord.IsHeld([Lb], 0));
    }
}

public sealed class AmbientTimeoutTests
{
    private static PowerModeSettings Power(string mode = PowerModes.ConsoleIdle, int displayMinutes = 10) =>
        PowerModeSettings.Default with { Mode = mode, DisplayMinutes = displayMinutes };

    private static AmbientSettings On(int minutes) => AmbientSettings.Default with { Enabled = true, IdleMinutes = minutes };

    [Theory]
    [InlineData(300, 600, true)]
    [InlineData(599, 600, true)]
    [InlineData(600, 600, false)]
    [InlineData(900, 600, false)]
    [InlineData(300, 0, true)]
    [InlineData(0, 600, false)]
    public void ScreensaverMustStartBeforeTheDisplayTurnsOff(int screensaver, int display, bool fine) =>
        Assert.Equal(fine, AmbientTimeouts.OrderProblem(screensaver, display) is null);

    [Fact]
    public void Validate_RefusesAScreenThatStartsAtOrAfterTheDisplayTimeout()
    {
        Assert.Null(On(5).Validate(Power(displayMinutes: 10)));
        Assert.NotNull(On(10).Validate(Power(displayMinutes: 10)));
        Assert.Contains("must start before the display turns off", On(12).Validate(Power(displayMinutes: 10)));
    }

    [Fact]
    public void Validate_NeedsConsoleIdleWhileOn_AndAllowsOffInAnyMode()
    {
        Assert.NotNull(On(5).Validate(Power(PowerModes.Balanced)));
        Assert.Null((On(5) with { Enabled = false }).Validate(Power(PowerModes.Balanced)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void Validate_RefusesAnIdleTimeOutsideTheRange(int minutes) =>
        Assert.NotNull(On(minutes).Validate(Power(displayMinutes: 720)));

    [Fact]
    public void Validate_RefusesAnInvalidChord() =>
        Assert.NotNull((On(5) with { Chord = "lb" }).Validate(Power()));

    [Fact]
    public void ChangingTheDisplayTimeout_ToAtMostTheScreenTime_IsRefused()
    {
        var store = new MemoryAmbientStore { Settings = On(5) };
        var service = new AmbientService(store, new FakeScreensaverFile(), NullLogger<AmbientService>.Instance);

        Assert.NotNull(service.DisplayTimeoutProblem(Power(displayMinutes: 5)));
        Assert.NotNull(service.DisplayTimeoutProblem(Power(displayMinutes: 3)));
        Assert.Null(service.DisplayTimeoutProblem(Power(displayMinutes: 6)));
        Assert.Null(service.DisplayTimeoutProblem(Power(PowerModes.Balanced, 3)));
    }

    [Fact]
    public void Card_FailsTheOrderCheckFromTheTimeoutWindowsActuallyHas()
    {
        var settings = On(5);
        var reading = new AmbientReading(settings, true, null, FakeScreensaverFile.Path, null);
        var power = Power(displayMinutes: 10);

        // Windows has a 15 minute screensaver (set by someone else) although 5 minutes were saved.
        var reported = new AmbientScreensaverState(true, FakeScreensaverFile.Path, 900, false);
        var card = GameReadyCards.AmbientScreen(reading, power, reported, elevated: true);

        Assert.Equal(GameReadyStates.Attention, card.State);
        Assert.Equal(GameReadyCheckStatuses.Fail, card.Checks.Single(check => check.Id == "ambientOrder").Status);
        Assert.Equal(GameReadyCheckStatuses.Fail, card.Checks.Single(check => check.Id == "ambientSet").Status);
    }

    [Fact]
    public void Card_IsReadyWhenWindowsHoldsTheSavedTimeout()
    {
        var reading = new AmbientReading(On(5), true, null, FakeScreensaverFile.Path, "Pulse-Eight USB-CEC Adapter");
        var reported = new AmbientScreensaverState(true, FakeScreensaverFile.Path, 300, false);

        var card = GameReadyCards.AmbientScreen(reading, Power(displayMinutes: 10), reported, elevated: true);

        Assert.Equal(GameReadyStates.Ready, card.State);
        Assert.All(card.Checks, check => Assert.Equal(GameReadyCheckStatuses.Pass, check.Status));
        Assert.Contains(card.Checks, check => check.Id == "ambientCec" && check.Detail!.Contains("Pulse-Eight"));
    }

    [Fact]
    public void Card_ReportsGroupPolicy()
    {
        var reading = new AmbientReading(On(5), true, null, FakeScreensaverFile.Path, null);
        var reported = new AmbientScreensaverState(true, @"C:\Windows\scrnsave.scr", 600, true);

        var card = GameReadyCards.AmbientScreen(reading, Power(), reported, elevated: true);

        Assert.Contains("Group Policy", card.Checks.Single(check => check.Id == "ambientSet").Detail);
    }
}

public sealed class AmbientServiceTests
{
    [Fact]
    public void Apply_InstallsTheFileSavesAndNormalizesTheChord()
    {
        var store = new MemoryAmbientStore();
        var file = new FakeScreensaverFile();
        var service = new AmbientService(store, file, NullLogger<AmbientService>.Instance);
        var power = PowerModeSettings.Default with { Mode = PowerModes.ConsoleIdle };

        service.Apply(AmbientSettings.Default with { Enabled = true, Chord = "RB + LB + Start" }, power);

        Assert.Equal(1, file.Installs);
        Assert.Equal("lb+rb+start", store.Settings!.Chord);
        Assert.True(store.Settings.Enabled);
    }

    [Fact]
    public void Apply_SavesNothingWhenTheFileCannotBeInstalled()
    {
        var store = new MemoryAmbientStore();
        var service = new AmbientService(store, new FakeScreensaverFile("needs the service"), NullLogger<AmbientService>.Instance);
        var power = PowerModeSettings.Default with { Mode = PowerModes.ConsoleIdle };

        var error = Assert.Throws<AmbientException>(() => service.Apply(AmbientSettings.Default with { Enabled = true }, power));

        Assert.Equal("needs the service", error.Message);
        Assert.Null(store.Settings);
    }

    [Fact]
    public void Store_RoundTripsThroughAFile()
    {
        var folder = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var store = new AmbientStore(folder);
            Assert.Null(store.Load());

            var settings = AmbientSettings.Default with { Enabled = true, SoftLock = true, IdleMinutes = 7 };
            store.Save(settings);

            Assert.Equal(settings, store.Load());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CecDetector_FindsAPulseEightAdapterOnly()
    {
        PnpNode[] nodes =
        [
            new("USB Composite Device", @"USB\VID_045E&PID_02FD\1", null, null),
            new("Pulse-Eight USB-CEC Adapter", @"USB\VID_2548&PID_1002\6&1", null, null),
        ];

        Assert.Equal("Pulse-Eight USB-CEC Adapter", CecDetector.Find(nodes)?.Name);
        Assert.Null(CecDetector.Find(nodes.Take(1)));
    }

    [Theory]
    [InlineData("pid_1234_luid_0x0_0x1_phys_0_eng_0_engtype_3D", 1234)]
    [InlineData("PID_7_luid_0x0", 7)]
    [InlineData("luid_0x0_pid_5", null)]
    [InlineData("pid__luid", null)]
    public void GpuInstanceNames_YieldTheProcessId(string name, int? expected) =>
        Assert.Equal(expected, AmbientGpuLoad.ProcessOf(name));

    [Theory]
    [InlineData(new[] { "ambient", "/s" }, "Screensaver")]
    [InlineData(new[] { "/s" }, "Host")]
    public void CommandLine_RunsTheScreensaverForTheAmbientVerb(string[] args, string expected) =>
        Assert.Equal(Enum.Parse<HostMode>(expected), HostCommandLine.Parse(args, startedFromConsole: false).Mode);

    [Fact]
    public void CommandLine_RunsTheScreensaverForAScrExecutable()
    {
        Assert.Equal(HostMode.Screensaver, HostCommandLine.Parse(["/s"], false, @"C:\Program Files\HyperHarbor\HyperHarbor Ambient.scr").Mode);
        Assert.Equal(HostMode.Launcher, HostCommandLine.Parse([], false, @"C:\Program Files\HyperHarbor\HyperHarbor.Host.exe").Mode);
    }
}

public sealed class AmbientDismissalTests
{
    private const ushort Lb = 0x0100;
    private const ushort Rb = 0x0200;
    private const ushort Start = 0x0010;
    private const ushort A = 0x1000;

    private sealed class FakeButtons : IControllerButtons
    {
        public ControllerSample? Current { get; set; }

        public ControllerSample? Sample() => Current;

        public void Press(ushort buttons) => Current = new ControllerSample(buttons, [buttons]);
    }

    private sealed class FakeSignals : IAmbientSignals
    {
        public bool Running { get; set; } = true;

        public int Dismissed { get; private set; }

        public int Hints { get; private set; }

        public void Dismiss() => Dismissed++;

        public void ShowHint() => Hints++;
    }

    private readonly FakeButtons _pad = new();
    private readonly FakeSignals _signals = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero));

    private AmbientDismissal Create(bool softLock = false)
    {
        _pad.Press(0);
        return new AmbientDismissal(_pad, _signals, _time) { Enabled = true, SoftLock = softLock, Chord = AmbientChord.Parse("lb+rb+start")!.Value };
    }

    [Fact]
    public void AControllerChange_EndsTheScreen()
    {
        var dismissal = Create();
        dismissal.Tick(); // the starting point
        Assert.Equal(0, _signals.Dismissed);

        _pad.Press(A);
        dismissal.Tick();

        Assert.Equal(1, _signals.Dismissed);
        Assert.Equal(1, dismissal.Dismissed);
    }

    [Fact]
    public void NoChange_DoesNothing()
    {
        var dismissal = Create();
        dismissal.Tick();
        dismissal.Tick();
        dismissal.Tick();

        Assert.Equal(0, _signals.Dismissed);
    }

    [Fact]
    public void NothingHappens_WhenTheScreenIsNotRunning_OrNotEnabled()
    {
        var dismissal = Create();
        dismissal.Tick();
        _signals.Running = false;
        _pad.Press(A);
        dismissal.Tick();
        Assert.Equal(0, _signals.Dismissed);

        _signals.Running = true;
        dismissal.Enabled = false;
        _pad.Press(Start);
        dismissal.Tick();
        Assert.Equal(0, _signals.Dismissed);
    }

    [Fact]
    public void NoController_DoesNothing()
    {
        var dismissal = Create();
        _pad.Current = null;
        dismissal.Tick();
        _pad.Press(A);
        dismissal.Tick();

        // The press right after the controller appeared is its starting point, not a change.
        Assert.Equal(0, _signals.Dismissed);
    }

    [Fact]
    public void WhileTheDisplayIsOff_ThePressOnlyWakesTheDisplay()
    {
        var dismissal = Create();
        dismissal.Tick();
        dismissal.DisplayOff = true;

        _pad.Press(A);
        dismissal.Tick();

        Assert.Equal(0, _signals.Dismissed);
    }

    [Fact]
    public void AfterTheDisplayComesOn_TheScreenStaysBrieflyThenAPressEndsIt()
    {
        var dismissal = Create();
        dismissal.Tick();
        dismissal.DisplayOff = true;
        dismissal.Tick();
        dismissal.DisplayOff = false;

        // The press that woke the TV is still being held or released, then a button is pressed within the grace.
        _pad.Press(A);
        dismissal.Tick();
        _time.Advance(TimeSpan.FromSeconds(1));
        _pad.Press(Start);
        dismissal.Tick();
        Assert.Equal(0, _signals.Dismissed);

        _time.Advance(AmbientDismissal.WakeGrace);
        _pad.Press(A);
        dismissal.Tick();
        Assert.Equal(1, _signals.Dismissed);
    }

    [Fact]
    public void SoftLock_IgnoresOtherButtons_AndShowsTheHint()
    {
        var dismissal = Create(softLock: true);
        dismissal.Tick();

        _pad.Press(A);
        dismissal.Tick();

        Assert.Equal(0, _signals.Dismissed);
        Assert.Equal(1, _signals.Hints);
    }

    [Fact]
    public void SoftLock_EndsTheScreenWhenTheChordIsHeld()
    {
        var dismissal = Create(softLock: true);
        dismissal.Tick();

        _pad.Press(Lb);
        dismissal.Tick();
        _pad.Press(Lb | Rb);
        dismissal.Tick();
        Assert.Equal(0, _signals.Dismissed);

        _pad.Press(Lb | Rb | Start);
        dismissal.Tick();
        Assert.Equal(1, _signals.Dismissed);
    }

    [Fact]
    public void SoftLock_AsksForTheHintAtMostOnceEveryFewSeconds()
    {
        var dismissal = Create(softLock: true);
        dismissal.Tick();

        _pad.Press(A);
        dismissal.Tick();
        _pad.Press(0);
        dismissal.Tick();
        Assert.Equal(1, _signals.Hints);

        _time.Advance(AmbientDismissal.HintGap);
        _pad.Press(A);
        dismissal.Tick();
        Assert.Equal(2, _signals.Hints);
    }

    [Fact]
    public void WithoutTheSoftLock_AnyButtonEndsTheScreen()
    {
        var dismissal = Create(softLock: false);
        dismissal.Tick();
        _pad.Press(A);
        dismissal.Tick();

        Assert.Equal(1, _signals.Dismissed);
        Assert.Equal(0, _signals.Hints);
    }
}

public sealed class AmbientScreensaverApplierTests
{
    private const string Ours = FakeScreensaverFile.Path;

    private sealed class FakeSettings : IScreensaverSettings
    {
        public ScreensaverSettings Current { get; set; } = new(false, null, 0, false, false);

        public int Writes { get; private set; }

        public ScreensaverSettings Read() => Current;

        public void Write(bool active, string? path, int timeoutSeconds, bool secure)
        {
            Writes++;
            Current = new ScreensaverSettings(active && path is not null, path, timeoutSeconds, secure, Current.PolicyLocked);
        }
    }

    private sealed class MemoryBackup : IScreensaverBackup
    {
        public ScreensaverSettings? Saved { get; set; }

        public ScreensaverSettings? Load() => Saved;

        public void Save(ScreensaverSettings settings) => Saved = settings;

        public void Clear() => Saved = null;
    }

    private readonly FakeSettings _settings = new();
    private readonly MemoryBackup _backup = new();

    private AmbientScreensaverApplier Create() => new(_settings, _backup);

    [Fact]
    public void SetsTheScreensaverKeepsTheUsersOwn_AndIsIdempotent()
    {
        var own = new ScreensaverSettings(true, @"C:\Windows\System32\Ribbons.scr", 900, true, false);
        _settings.Current = own;
        var applier = Create();

        var state = applier.Sync(Ours, 300);
        var writes = _settings.Writes;
        applier.Sync(Ours, 300);

        Assert.Equal(new AmbientScreensaverState(true, Ours, 300, false), state);
        Assert.Equal(own, _backup.Saved);
        Assert.Equal(writes, _settings.Writes);
        Assert.False(_settings.Current.Secure);
    }

    [Fact]
    public void WhenTurnedOff_PutsTheUsersScreensaverBack()
    {
        var own = new ScreensaverSettings(true, @"C:\Windows\System32\Ribbons.scr", 900, false, false);
        _settings.Current = own;
        var applier = Create();
        applier.Sync(Ours, 300);

        var state = applier.Sync(null, 0);

        Assert.Equal(own, _settings.Current);
        Assert.Null(_backup.Saved);
        Assert.Equal(900, state.TimeoutSeconds);
    }

    [Fact]
    public void WhenTurnedOff_LeavesAScreensaverThePersonChoseSince()
    {
        var applier = Create();
        applier.Sync(Ours, 300);
        _settings.Current = new ScreensaverSettings(true, @"C:\Windows\System32\Bubbles.scr", 600, false, false);

        applier.Sync(null, 0);

        Assert.Equal(@"C:\Windows\System32\Bubbles.scr", _settings.Current.Path);
        Assert.Null(_backup.Saved);
    }

    [Fact]
    public void TurnsOffAnOrphanedAmbientScreensaverWithoutABackup()
    {
        _settings.Current = new ScreensaverSettings(true, Ours, 300, false, false);

        var state = Create().Sync(null, 0);

        Assert.False(state.Active);
        Assert.Null(state.Path);
    }

    [Fact]
    public void ChangesNothingUnderGroupPolicy()
    {
        _settings.Current = new ScreensaverSettings(true, @"C:\Windows\System32\Ribbons.scr", 900, false, true);

        var state = Create().Sync(Ours, 300);

        Assert.Equal(0, _settings.Writes);
        Assert.True(state.PolicyLocked);
        Assert.Null(_backup.Saved);
    }

    [Fact]
    public void NeverBacksUpTheAmbientScreensaverAsTheUsersOwn()
    {
        _settings.Current = new ScreensaverSettings(true, Ours, 600, false, false);

        Create().Sync(Ours, 300);

        Assert.Null(_backup.Saved);
    }
}

public sealed class AmbientMeterTests
{
    [Fact]
    public void CpuPercent_IsTheShareOfAllProcessors()
    {
        // One second of processor time over 10 seconds on 4 processors is 2.5 percent.
        Assert.Equal(2.5, AmbientMeter.CpuPercent(10_000_000, TimeSpan.FromSeconds(10), 4));
        Assert.Equal(0, AmbientMeter.CpuPercent(5, TimeSpan.Zero, 4));
        Assert.Equal(100, AmbientMeter.CpuPercent(ulong.MaxValue / 2, TimeSpan.FromSeconds(1), 1));
    }

    [Fact]
    public void LeavesOutTheStartUp_AndReportsAfterAMinute()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 12, 20, 0, 0, TimeSpan.Zero));
        var meter = new AmbientMeter(time, 4);

        // Start-up work: a lot of processor time in the first seconds.
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(meter.Tick(500_000_000));

        time.Advance(AmbientMeter.Settle);
        Assert.Null(meter.Tick(600_000_000)); // the window starts here

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(meter.Tick(600_000_000));

        // 0.8 s of processor time over the 60 s window on 4 processors.
        time.Advance(TimeSpan.FromSeconds(30));
        var report = meter.Tick(608_000_000);

        Assert.NotNull(report);
        Assert.Equal(0.3, report!.Value.CpuPercent);
        Assert.Equal(60, report.Value.Seconds);
    }
}
