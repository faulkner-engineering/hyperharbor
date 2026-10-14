using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.GameAware;

public sealed class GamingDetectorTests
{
    private static ForegroundSignals Signals(
        int? state = null, bool covers = false, bool border = true, bool shell = false, bool own = false, string? name = "app", string[]? lineage = null) =>
        new(state, covers, border, shell, own, name, lineage ?? []);

    private static GamingVerdict Evaluate(ForegroundSignals signals, string[]? allow = null, string[]? ignore = null) =>
        GamingDetector.Evaluate(signals, allow ?? [], ignore ?? []);

    [Fact]
    public void AFullScreenDirect3dApp_IsAGame_WithItsName()
    {
        var verdict = Evaluate(Signals(state: 3, name: "eldenring"));

        Assert.True(verdict.Gaming);
        Assert.Equal("eldenring", verdict.Process);
    }

    [Fact]
    public void PresentationMode_CountsUnlessIgnored()
    {
        Assert.True(Evaluate(Signals(state: 4, name: "powerpnt")).Gaming);
        Assert.False(Evaluate(Signals(state: 4, name: "powerpnt"), ignore: ["powerpnt"]).Gaming);
    }

    [Fact]
    public void ABorderlessFullScreenWindow_NeedsALauncherInItsLineage()
    {
        Assert.True(Evaluate(Signals(covers: true, border: false, name: "game", lineage: ["steam", "explorer"])).Gaming);
        Assert.False(Evaluate(Signals(covers: true, border: false, name: "vlc", lineage: ["explorer"])).Gaming);
    }

    [Fact]
    public void AMaximizedWindowWithABorder_IsNotAGame_EvenFromALauncher()
    {
        Assert.False(Evaluate(Signals(covers: true, border: true, name: "game", lineage: ["steam"])).Gaming);
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("epicgameslauncher")]
    [InlineData("battle.net")]
    [InlineData("playnite.fullscreenapp")]
    public void ALaunchersOwnWindow_IsNeverTheGame(string launcher)
    {
        Assert.False(Evaluate(Signals(state: 3, covers: true, border: false, name: launcher)).Gaming);
    }

    [Fact]
    public void TheAllowlist_MakesAnyForegroundProgramAGame()
    {
        var verdict = Evaluate(Signals(name: "emulator"), allow: ["emulator"]);

        Assert.True(verdict.Gaming);
        Assert.Contains("allowlist", verdict.Reason);
    }

    [Fact]
    public void TheIgnoreList_BeatsTheAllowlistAndEverythingElse()
    {
        Assert.False(Evaluate(Signals(state: 3, name: "emulator"), allow: ["emulator"], ignore: ["emulator"]).Gaming);
    }

    [Fact]
    public void OwnWindowsAndTheShell_AreNeverGames()
    {
        Assert.False(Evaluate(Signals(state: 3, own: true, name: "hyperharbor.host")).Gaming);
        Assert.False(Evaluate(Signals(state: 3, covers: true, border: false, shell: true, name: "explorer"), allow: ["explorer"]).Gaming);
    }

    [Fact]
    public void WithoutAnySignal_ThereIsNoGame() => Assert.False(Evaluate(Signals(name: null)).Gaming);

    [Fact]
    public void ConfiguredExtraLaunchers_AreExcludedAndStartGames()
    {
        var launcher = GamingDetector.Evaluate(Signals(state: 3, name: "mylauncher"), [], [], ["mylauncher"]);
        var child = GamingDetector.Evaluate(Signals(covers: true, border: false, name: "game", lineage: ["mylauncher"]), [], [], ["mylauncher"]);

        Assert.False(launcher.Gaming);
        Assert.True(child.Gaming);
    }
}

public sealed class GamingDebouncerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Gaming_MustHoldThreeSecondsBeforeItStarts()
    {
        var debouncer = new GamingDebouncer();

        Assert.False(debouncer.Observe(true, Start));
        Assert.False(debouncer.Observe(true, Start.AddSeconds(2.9)));
        Assert.True(debouncer.Observe(true, Start.AddSeconds(3)));
    }

    [Fact]
    public void ABriefDip_RestartsTheEnterTimer()
    {
        var debouncer = new GamingDebouncer();
        debouncer.Observe(true, Start);
        debouncer.Observe(false, Start.AddSeconds(2));

        Assert.False(debouncer.Observe(true, Start.AddSeconds(3)));
        Assert.False(debouncer.Observe(true, Start.AddSeconds(5.9)));
        Assert.True(debouncer.Observe(true, Start.AddSeconds(6)));
    }

    [Fact]
    public void Gaming_EndsOnlyAfterTenSecondsWithoutAGame()
    {
        var debouncer = new GamingDebouncer();
        debouncer.Observe(true, Start);
        debouncer.Observe(true, Start.AddSeconds(3));

        Assert.True(debouncer.Observe(false, Start.AddSeconds(4)));
        Assert.True(debouncer.Observe(false, Start.AddSeconds(13.9)));
        Assert.False(debouncer.Observe(false, Start.AddSeconds(14)));
    }

    [Fact]
    public void AlTabbingBack_KeepsGaming()
    {
        var debouncer = new GamingDebouncer();
        debouncer.Observe(true, Start);
        debouncer.Observe(true, Start.AddSeconds(3));
        debouncer.Observe(false, Start.AddSeconds(5));
        debouncer.Observe(true, Start.AddSeconds(9));

        Assert.True(debouncer.Observe(false, Start.AddSeconds(10)));
        Assert.True(debouncer.Observe(false, Start.AddSeconds(19.9)));
        Assert.False(debouncer.Observe(false, Start.AddSeconds(20)));
    }
}

public sealed class GamePolicyPlannerTests
{
    private static Vm Running(string name, VmState state = VmState.Running) => FakeVmInventory.CreateVm(Guid.NewGuid(), name, state);

    private static GamePlanItem Single(GameVmInput input, GameAwareSettings? settings = null) =>
        Assert.Single(GamePolicyPlanner.Plan([input], settings ?? new GameAwareSettings()));

    [Fact]
    public void ThePolicyDefaultsToPause() => Assert.Equal(GameAction.Pause, Single(new GameVmInput(Running("a"), false, false)).Action);

    [Fact]
    public void SaveAndKeep_FollowTheDefaultAndPerVmPolicies()
    {
        var saved = Running("saved");
        var kept = Running("kept");
        var settings = new GameAwareSettings
        {
            DefaultPolicy = GamePolicy.Save,
            VmPolicies = new Dictionary<string, GameVmPolicy> { [kept.Id.ToString("D")] = new(GamePolicy.Keep) },
        };

        Assert.Equal(GameAction.Save, Single(new GameVmInput(saved, false, false), settings).Action);
        Assert.Equal(GameAction.Skip, Single(new GameVmInput(kept, false, false), settings).Action);
    }

    [Theory]
    [InlineData(GamePolicy.Save)]
    [InlineData(GamePolicy.Keep)]
    public void AGpuPartition_ForcesPause(GamePolicy chosen)
    {
        var vm = Running("gpu");
        var settings = new GameAwareSettings { VmPolicies = new Dictionary<string, GameVmPolicy> { [vm.Id.ToString("D")] = new(chosen) } };

        var item = Single(new GameVmInput(vm, GpuPartition: true, HasSession: false), settings);

        Assert.Equal(GameAction.Pause, item.Action);
        Assert.Contains("GPU partition", item.Reason);
    }

    [Fact]
    public void AVmWithASession_IsLeftRunning_UnlessItsPolicyAlwaysPauses()
    {
        var vm = Running("busy");
        var always = new GameAwareSettings { VmPolicies = new Dictionary<string, GameVmPolicy> { [vm.Id.ToString("D")] = new(GamePolicy.Pause, AlwaysPause: true) } };

        var skipped = Single(new GameVmInput(vm, false, true));
        var paused = Single(new GameVmInput(vm, false, true), always);

        Assert.Equal(GameAction.Skip, skipped.Action);
        Assert.True(skipped.SkippedForSession);
        Assert.Equal(GameAction.Pause, paused.Action);
    }

    [Fact]
    public void OnlyRunningVmsArePlanned()
    {
        var plan = GamePolicyPlanner.Plan(
            [new GameVmInput(Running("off", VmState.Off), false, false), new GameVmInput(Running("paused", VmState.Paused), false, false)],
            new GameAwareSettings());

        Assert.Empty(plan);
    }
}

public sealed class GameAwareControllerTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeVmInventory _inventory = new();
    private readonly GameAwareStore _store;
    private readonly Pauser _pauser;
    private readonly Invoker _invoker;
    private readonly HashSet<Guid> _sessions = [];
    private readonly HashSet<Guid> _gpu = [];
    private readonly GameAwareController _controller;

    public GameAwareControllerTests()
    {
        _store = new GameAwareStore(_directory);
        _pauser = new Pauser(_inventory);
        _invoker = new Invoker(_inventory);
        _controller = Create();
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private GameAwareController Create() => new(
        _store, _inventory, _pauser, _invoker,
        (vm, _) => Task.FromResult(_sessions.Contains(vm.Id)), id => _gpu.Contains(id), _time, NullLogger<GameAwareController>.Instance, _directory);

    private Vm AddVm(string name, VmState state = VmState.Running)
    {
        var vm = FakeVmInventory.CreateVm(Guid.NewGuid(), name, state);
        _inventory.Vms.Add(vm);
        return vm;
    }

    private VmState StateOf(Vm vm) => _inventory.Vms.Single(v => v.Id == vm.Id).State;

    private void Enable(GameAwareSettings? settings = null) => _store.Update(_ => (settings ?? new GameAwareSettings()) with { Enabled = true });

    private static GameStateReport Game(string process = "eldenring") => new(true, process, "full-screen");

    private static GameStateReport NoGame() => new(false, null, null);

    [Fact]
    public async Task WhenOff_AGameChangesNothing()
    {
        var vm = AddVm("a");

        await _controller.ReportAsync(Game(), default);

        Assert.Equal(VmState.Running, StateOf(vm));
        Assert.False(_controller.Status().Gaming);
    }

    [Fact]
    public async Task AGameStarting_PausesEveryRunningVm_AndEndingItResumesThem()
    {
        Enable();
        var a = AddVm("a");
        var b = AddVm("b");
        var off = AddVm("off", VmState.Off);

        await _controller.ReportAsync(Game(), default);

        Assert.Equal(VmState.Paused, StateOf(a));
        Assert.Equal(VmState.Paused, StateOf(b));
        Assert.Equal(VmState.Off, StateOf(off));
        var status = _controller.Status();
        Assert.True(status.Gaming);
        Assert.Equal("eldenring", status.Process);
        Assert.Equal(["a", "b"], status.ChangedVms.Order());

        await _controller.ReportAsync(NoGame(), default);

        Assert.Equal(VmState.Running, StateOf(a));
        Assert.Equal(VmState.Running, StateOf(b));
        Assert.Equal(VmState.Off, StateOf(off));
        Assert.Empty(_controller.Status().ChangedVms);
        Assert.False(_controller.Status().Gaming);
    }

    [Fact]
    public async Task OnlyWhatTheHostChanged_IsResumed()
    {
        Enable();
        var mine = AddVm("mine");
        var alreadyPaused = AddVm("paused-by-user", VmState.Paused);
        var userStopsIt = AddVm("stopped-meanwhile");

        await _controller.ReportAsync(Game(), default);
        _inventory.Vms[_inventory.Vms.FindIndex(v => v.Id == userStopsIt.Id)] = userStopsIt with { State = VmState.Off };
        await _controller.ReportAsync(NoGame(), default);

        Assert.Equal(VmState.Running, StateOf(mine));
        Assert.Equal(VmState.Paused, StateOf(alreadyPaused));
        Assert.Equal(VmState.Off, StateOf(userStopsIt));
        Assert.DoesNotContain(alreadyPaused.Id, _pauser.Resumed);
        Assert.DoesNotContain(userStopsIt.Id, _pauser.Resumed);
    }

    [Fact]
    public async Task ASavedVm_IsStartedAgainAfterTheGame()
    {
        Enable(new GameAwareSettings { DefaultPolicy = GamePolicy.Save });
        var vm = AddVm("a");

        await _controller.ReportAsync(Game(), default);
        Assert.Equal(VmState.Saved, StateOf(vm));
        await _controller.ReportAsync(NoGame(), default);

        Assert.Equal(VmState.Running, StateOf(vm));
        Assert.Equal([VmAction.Save, VmAction.Start], _invoker.Actions);
    }

    [Fact]
    public async Task AGpuVm_IsPausedEvenWhenItsPolicySaves()
    {
        var vm = AddVm("gpu");
        _gpu.Add(vm.Id);
        Enable(new GameAwareSettings { DefaultPolicy = GamePolicy.Save });

        await _controller.ReportAsync(Game(), default);

        Assert.Equal(VmState.Paused, StateOf(vm));
        Assert.Empty(_invoker.Actions);
    }

    [Fact]
    public async Task AVmWithASession_StaysRunning_AndIsNamedForTheClientNotice()
    {
        Enable();
        var busy = AddVm("busy");
        var idle = AddVm("idle");
        _sessions.Add(busy.Id);

        await _controller.ReportAsync(Game(), default);

        Assert.Equal(VmState.Running, StateOf(busy));
        Assert.Equal(VmState.Paused, StateOf(idle));
        Assert.Equal(["busy"], _controller.Status().SessionVms);
    }

    [Fact]
    public async Task TheWatchdog_ResumesWhenTheTrayStopsReporting()
    {
        Enable();
        var vm = AddVm("a");
        await _controller.ReportAsync(Game(), default);

        _time.Advance(TimeSpan.FromSeconds(59));
        await _controller.TickAsync(default);
        Assert.Equal(VmState.Paused, StateOf(vm));

        _time.Advance(TimeSpan.FromSeconds(1));
        await _controller.TickAsync(default);

        Assert.Equal(VmState.Running, StateOf(vm));
        Assert.False(_controller.Status().Gaming);
    }

    [Fact]
    public async Task ARepeatedGameReport_KeepsTheWatchdogQuiet()
    {
        Enable();
        var vm = AddVm("a");
        await _controller.ReportAsync(Game(), default);

        for (var i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await _controller.ReportAsync(Game(), default);
            await _controller.TickAsync(default);
        }

        Assert.Equal(VmState.Paused, StateOf(vm));
        Assert.Single(_pauser.Paused);
    }

    [Fact]
    public async Task TurningTheModeOff_DuringAGame_ResumesAtTheNextTick()
    {
        Enable();
        var vm = AddVm("a");
        await _controller.ReportAsync(Game(), default);

        _store.Update(settings => settings with { Enabled = false });
        await _controller.TickAsync(default);

        Assert.Equal(VmState.Running, StateOf(vm));
    }

    [Fact]
    public async Task ANewController_ResumesWhatAnEarlierRunLeftChanged()
    {
        Enable();
        var vm = AddVm("a");
        await _controller.ReportAsync(Game(), default);

        var restarted = Create();
        await restarted.TickAsync(default);

        Assert.Equal(VmState.Running, StateOf(vm));
        Assert.False(File.Exists(Path.Combine(_directory, GameAwareController.ChangedFileName)));
    }

    [Fact]
    public async Task AFailedResume_StaysOnTheListAndIsRetried()
    {
        Enable();
        var vm = AddVm("a");
        await _controller.ReportAsync(Game(), default);
        _pauser.FailResume = true;

        await _controller.ReportAsync(NoGame(), default);
        Assert.Equal(VmState.Paused, StateOf(vm));
        Assert.Equal(["a"], _controller.Status().ChangedVms);

        _pauser.FailResume = false;
        await _controller.TickAsync(default);

        Assert.Equal(VmState.Running, StateOf(vm));
    }

    [Fact]
    public async Task AFailedPause_DoesNotStopTheOtherVms_AndIsNotListedAsChanged()
    {
        Enable();
        var first = AddVm("first");
        var second = AddVm("second");
        _pauser.FailFor.Add(first.Id);

        await _controller.ReportAsync(Game(), default);

        Assert.Equal(VmState.Running, StateOf(first));
        Assert.Equal(VmState.Paused, StateOf(second));
        Assert.Equal(["second"], _controller.Status().ChangedVms);
    }

    private readonly List<bool> _active = [];

    private GameAwareMonitor Monitor() => new(_store, _controller, _active.Add, _time);

    private static ForegroundSignals FullScreen(string name) => new(3, true, false, false, false, name, []);

    private static ForegroundSignals Desktop() => new(null, false, true, true, false, "explorer", []);

    [Fact]
    public async Task TheMonitor_EntersAfterThreeSeconds_AndLeavesAfterTen()
    {
        Enable();
        var vm = AddVm("a");
        var monitor = Monitor();

        await monitor.OnSignalsAsync(FullScreen("eldenring"), default);
        _time.Advance(TimeSpan.FromSeconds(2));
        await monitor.OnSignalsAsync(FullScreen("eldenring"), default);
        Assert.Equal(VmState.Running, StateOf(vm));

        _time.Advance(TimeSpan.FromSeconds(1));
        await monitor.OnSignalsAsync(FullScreen("eldenring"), default);
        Assert.Equal(VmState.Paused, StateOf(vm));
        Assert.True(_active[^1]);

        await monitor.OnSignalsAsync(Desktop(), default);
        _time.Advance(TimeSpan.FromSeconds(9));
        await monitor.OnSignalsAsync(Desktop(), default);
        Assert.Equal(VmState.Paused, StateOf(vm));

        _time.Advance(TimeSpan.FromSeconds(1));
        await monitor.OnSignalsAsync(Desktop(), default);
        Assert.Equal(VmState.Running, StateOf(vm));
        Assert.False(_active[^1]);
    }

    [Fact]
    public async Task AUserAddedLauncher_MakesItsBorderlessChildrenGames_ButNotItsOwnWindow()
    {
        Enable();
        _store.Update(s => s with { ExtraLaunchers = ["Itch.EXE", "itch.exe", "  "] });
        var vm = AddVm("a");
        var monitor = Monitor();
        var child = new ForegroundSignals(null, true, false, false, false, "indiegame", ["itch", "explorer"]);
        var launcher = new ForegroundSignals(3, true, false, false, false, "itch", []);

        Assert.Equal(["itch"], _store.Load().ExtraLaunchers);
        await monitor.OnSignalsAsync(launcher, default);
        _time.Advance(TimeSpan.FromSeconds(3));
        await monitor.OnSignalsAsync(launcher, default);
        Assert.Equal(VmState.Running, StateOf(vm));

        await monitor.OnSignalsAsync(child, default);
        _time.Advance(TimeSpan.FromSeconds(3));
        await monitor.OnSignalsAsync(child, default);
        Assert.Equal(VmState.Paused, StateOf(vm));
    }

    [Fact]
    public async Task NotAGame_IgnoresTheProgram_AndResumesAtOnce()
    {
        Enable();
        var vm = AddVm("a");
        var monitor = Monitor();
        await monitor.OnSignalsAsync(FullScreen("Powerpnt"), default);
        _time.Advance(TimeSpan.FromSeconds(3));
        await monitor.OnSignalsAsync(FullScreen("Powerpnt"), default);
        Assert.Equal(VmState.Paused, StateOf(vm));

        var added = await monitor.NotAGameAsync(default);

        Assert.Equal("Powerpnt", added);
        Assert.Equal(["powerpnt"], _store.Load().IgnoreList);
        Assert.Equal(VmState.Running, StateOf(vm));
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(2));
            await monitor.OnSignalsAsync(FullScreen("powerpnt"), default);
        }

        Assert.Equal(VmState.Running, StateOf(vm));
    }

    [Fact]
    public async Task NotAGame_WithNoGameActive_ChangesNothing()
    {
        Assert.Null(await Monitor().NotAGameAsync(default));
        Assert.Empty(_store.Load().IgnoreList);
    }

    [Fact]
    public async Task TheMonitor_ReportsAGameToTheSleepAndBackupGatesEvenWhenTheModeIsOff()
    {
        var vm = AddVm("a");
        var monitor = Monitor();

        await monitor.OnSignalsAsync(FullScreen("game"), default);
        _time.Advance(TimeSpan.FromSeconds(3));
        await monitor.OnSignalsAsync(FullScreen("game"), default);

        Assert.True(_active[^1]);
        Assert.Equal(VmState.Running, StateOf(vm));
    }

    private sealed class Pauser(FakeVmInventory inventory) : IVmPauser
    {
        public List<Guid> Paused { get; } = [];

        public List<Guid> Resumed { get; } = [];

        public HashSet<Guid> FailFor { get; } = [];

        public bool FailResume { get; set; }

        public Task PauseAsync(Guid vmId, CancellationToken cancellationToken)
        {
            if (FailFor.Contains(vmId))
            {
                throw new InvalidOperationException("Hyper-V refused.");
            }

            Paused.Add(vmId);
            Set(vmId, VmState.Paused);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(Guid vmId, CancellationToken cancellationToken)
        {
            if (FailResume)
            {
                throw new InvalidOperationException("Hyper-V refused.");
            }

            Resumed.Add(vmId);
            Set(vmId, VmState.Running);
            return Task.CompletedTask;
        }

        private void Set(Guid id, VmState state)
        {
            var index = inventory.Vms.FindIndex(v => v.Id == id);
            inventory.Vms[index] = inventory.Vms[index] with { State = state };
        }
    }

    private sealed class Invoker(FakeVmInventory inventory) : IHyperVPowerInvoker
    {
        public List<VmAction> Actions { get; } = [];

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            var index = inventory.Vms.FindIndex(v => v.Id == vmId);
            inventory.Vms[index] = inventory.Vms[index] with { State = action == VmAction.Save ? VmState.Saved : VmState.Running };
            return Task.CompletedTask;
        }
    }
}

public sealed class GameAwareStoreTests : IDisposable
{
    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ItStartsOff_WithPauseAsTheDefault()
    {
        var settings = new GameAwareStore(_directory).Load();

        Assert.False(settings.Enabled);
        Assert.Equal(GamePolicy.Pause, settings.DefaultPolicy);
    }

    [Fact]
    public void ProcessNames_AreNormalizedAndDeduplicated()
    {
        var store = new GameAwareStore(_directory);

        var saved = store.Update(s => s with { Allowlist = [@"C:\Games\Foo.EXE", "foo", " Bar ", ""], IgnoreList = ["VLC.exe"] });

        Assert.Equal(["foo", "bar"], saved.Allowlist);
        Assert.Equal(["vlc"], saved.IgnoreList);
    }

    [Fact]
    public void SettingsRoundTripThroughTheFile()
    {
        var id = Guid.NewGuid();
        new GameAwareStore(_directory).Update(s => s with
        {
            Enabled = true,
            DefaultPolicy = GamePolicy.Save,
            VmPolicies = new Dictionary<string, GameVmPolicy> { [id.ToString("D")] = new(GamePolicy.Keep, true) },
        });

        var loaded = new GameAwareStore(_directory).Load();

        Assert.True(loaded.Enabled);
        Assert.Equal(GamePolicy.Save, loaded.DefaultPolicy);
        Assert.Equal(new GameVmPolicy(GamePolicy.Keep, true), loaded.PolicyFor(id));
        Assert.Equal(GamePolicy.Save, loaded.PolicyFor(Guid.NewGuid()).Policy);
    }
}
