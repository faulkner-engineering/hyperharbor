using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class BackupCoordinatorTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000002");
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 1, 0, 0, TimeSpan.Zero));
    private readonly FakeVmInventory _inventory = new();
    private readonly BackupConfigStore _config;
    private readonly BackupHistoryStore _history;
    private readonly RecordingRunner _runner;
    private readonly VmJobStore _jobs;
    private readonly BackupCoordinator _coordinator;
    private bool _game;
    private bool _session;
    private int _holds;
    private bool _gateClosed;

    public BackupCoordinatorTests()
    {
        _config = new BackupConfigStore(_data);
        _history = new BackupHistoryStore(_data);
        _runner = new RecordingRunner(_history);
        _jobs = new VmJobStore(new VmOperationLocks(), _time, NullLogger<VmJobStore>.Instance);
        _inventory.Vms.Add(new Vm(VmId, "Dev", VmState.Running, 100, 1, 1024, 2, true, ["10.0.0.5"]));
        _coordinator = new BackupCoordinator(
            _config, _inventory, new FakeSessions(() => _session), () => _game, _history, _runner, _jobs,
            () => _gateClosed ? null : Acquire(),
            _time, NullLogger<BackupCoordinator>.Instance);
    }

    public void Dispose()
    {
        _jobs.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    private static BackupTarget Target() => new()
    {
        Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B"),
        VmRepository = new RepositoryRef("r1", RepositoryKeyState.Confirmed),
        HostRepository = new RepositoryRef("r2", RepositoryKeyState.Confirmed),
    };

    private void Configure(BackupPolicy? policy = null, BackupTarget? target = null) =>
        _config.Update(_ => new BackupConfig
        {
            Targets = [target ?? Target()],
            Policies = policy is null ? [] : [policy],
        });

    private static BackupPolicy Policy(BackupTriggers? triggers = null, RunConditions? conditions = null) =>
        new() { VmId = VmId.ToString("D"), TargetId = "t1", Triggers = triggers ?? new BackupTriggers(), Conditions = conditions ?? RunConditions.Default };

    private async Task WaitForRunsAsync(int count)
    {
        for (var i = 0; i < 500 && (_runner.Vm.Count + _runner.HostRuns < count || Volatile.Read(ref _holds) != 0); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task A_due_schedule_slot_starts_the_VM_backup_and_the_host_state_backup_once()
    {
        Configure(Policy());
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));

        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(2);

        var request = Assert.Single(_runner.Vm);
        Assert.Equal(VmId, request.VmId);
        Assert.Equal(BackupTrigger.Schedule, request.Trigger);
        Assert.Equal(1, _runner.HostRuns);
        Assert.Equal(0, Volatile.Read(ref _holds));
    }

    [Fact]
    public async Task Nothing_starts_before_the_slot_or_for_a_VM_without_a_policy()
    {
        Configure();
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 1, 30, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(1);

        Assert.Empty(_runner.Vm);
        Assert.Equal(1, _runner.HostRuns);
    }

    [Fact]
    public async Task A_served_slot_is_not_run_again_on_the_next_tick()
    {
        Configure(Policy());
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(2);
        _time.Advance(TimeSpan.FromMinutes(1));

        await _coordinator.TickAsync(default);
        await Task.Delay(50);

        Assert.Single(_runner.Vm);
        Assert.Equal(1, _runner.HostRuns);
    }

    [Fact]
    public async Task A_game_defers_the_run_until_it_ends()
    {
        Configure(Policy());
        _game = true;
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);
        _time.Advance(TimeSpan.FromMinutes(30));
        await _coordinator.TickAsync(default);
        await Task.Delay(50);
        Assert.Empty(_runner.Vm);
        Assert.True(_coordinator.WaitingCount > 0);

        _game = false;
        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(2);

        Assert.Single(_runner.Vm);
    }

    [Fact]
    public async Task An_active_session_defers_a_VM_run_but_not_the_host_state_run()
    {
        Configure(Policy());
        _session = true;
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));

        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(1);

        Assert.Empty(_runner.Vm);
        Assert.Equal(1, _runner.HostRuns);
    }

    [Fact]
    public async Task A_run_that_never_gets_its_conditions_is_recorded_as_skipped_after_the_limit()
    {
        Configure(Policy());
        _game = true;
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);

        _time.Advance(BackupCoordinator.DeferLimit + TimeSpan.FromMinutes(1));
        await _coordinator.TickAsync(default);

        var skipped = _history.List().Where(r => r.Outcome == BackupOutcome.Skipped).ToList();
        Assert.Equal(2, skipped.Count);
        Assert.All(skipped, r => Assert.Equal("conditionsNotMet", r.Reason));
        Assert.Equal(0, _coordinator.WaitingCount);
        Assert.Empty(_runner.Vm);
    }

    [Fact]
    public async Task A_shutdown_runs_after_the_debounce_when_the_policy_asks_for_it_and_conditions_hold()
    {
        Configure(Policy(new BackupTriggers(Schedule: false, OnShutdown: true, DebounceMinutes: 10)));
        await _coordinator.TickAsync(default);
        _inventory.SetState(VmId, VmState.Off);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _coordinator.TickAsync(default);
        _time.Advance(TimeSpan.FromMinutes(5));
        await _coordinator.TickAsync(default);
        await Task.Delay(50);
        Assert.Empty(_runner.Vm);

        _time.Advance(TimeSpan.FromMinutes(6));
        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(1);

        Assert.Equal(BackupTrigger.Shutdown, Assert.Single(_runner.Vm).Trigger);
    }

    [Fact]
    public async Task A_restart_within_the_debounce_cancels_the_shutdown_run()
    {
        Configure(Policy(new BackupTriggers(Schedule: false, OnShutdown: true, DebounceMinutes: 10)));
        await _coordinator.TickAsync(default);
        _inventory.SetState(VmId, VmState.Off);
        _time.Advance(TimeSpan.FromMinutes(1));
        await _coordinator.TickAsync(default);
        _inventory.SetState(VmId, VmState.Running);
        _time.Advance(TimeSpan.FromMinutes(2));
        await _coordinator.TickAsync(default);

        _time.Advance(TimeSpan.FromMinutes(30));
        await _coordinator.TickAsync(default);
        await Task.Delay(50);

        Assert.Empty(_runner.Vm);
    }

    [Fact]
    public async Task A_closed_update_gate_holds_the_run_back()
    {
        Configure(Policy());
        _gateClosed = true;
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 2, 1, 0, TimeSpan.Zero));
        await _coordinator.TickAsync(default);
        await Task.Delay(50);
        Assert.Empty(_runner.Vm);

        _gateClosed = false;
        await _coordinator.TickAsync(default);
        await WaitForRunsAsync(2);

        Assert.Single(_runner.Vm);
    }

    [Fact]
    public async Task A_failed_run_makes_the_job_fail_and_releases_the_hold()
    {
        Configure();
        _runner.Outcome = BackupOutcome.Failed;

        var job = _coordinator.StartManual("t1", null);
        await _jobs.WhenFinished(job.Id);

        Assert.Equal(VmJobState.Failed, _jobs.Get(job.Id, BackupCoordinator.HostUserId)!.State);
        Assert.Equal(0, Volatile.Read(ref _holds));
    }

    [Fact]
    public async Task A_manual_run_ignores_conditions_and_uses_the_target_defaults_without_a_policy()
    {
        Configure();
        _game = true;

        var job = _coordinator.StartManual("t1", VmId);
        await _jobs.WhenFinished(job.Id);

        var request = Assert.Single(_runner.Vm);
        Assert.Equal(BackupTrigger.Manual, request.Trigger);
        Assert.Equal(RetentionPolicy.Default, request.Policy.Retention);
    }

    [Fact]
    public async Task Run_all_starts_the_host_state_and_each_VM_with_a_policy_and_reports_what_cannot_start()
    {
        Configure(Policy());
        var other = new BackupTarget
        {
            Id = "t2", Name = "Pending", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:C"),
            VmRepository = new RepositoryRef("r3", RepositoryKeyState.PendingKey), HostRepository = new RepositoryRef("r4", RepositoryKeyState.PendingKey),
        };
        _config.Update(c => c with { Targets = [.. c.Targets, other] });
        _game = true;

        var result = _coordinator.StartAllManual();
        await WaitForRunsAsync(2);

        Assert.Equal(2, result.Started);
        Assert.Equal(VmId, Assert.Single(_runner.Vm).VmId);
        Assert.Equal(1, _runner.HostRuns);
        Assert.Contains("Pending", Assert.Single(result.Problems));
        Assert.Equal(0, Volatile.Read(ref _holds));
    }

    [Fact]
    public void Run_all_without_a_target_starts_nothing()
    {
        Assert.Equal(0, _coordinator.StartAllManual().Started);
    }

    [Fact]
    public void A_manual_run_for_an_unknown_target_is_refused()
    {
        Configure();

        Assert.Throws<BackupException>(() => _coordinator.StartManual("nope", null));
        Assert.Equal(0, Volatile.Read(ref _holds));
    }

    [Fact]
    public void A_manual_run_while_the_update_gate_is_closed_is_refused()
    {
        Configure();
        _gateClosed = true;

        Assert.Throws<BackupException>(() => _coordinator.StartManual("t1", null));
    }

    [Fact]
    public async Task Disabled_targets_and_unconfirmed_repositories_never_run()
    {
        Configure(Policy(), Target() with { VmRepository = new RepositoryRef("r1", RepositoryKeyState.PendingKey), HostRepository = null });
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 3, 0, 0, TimeSpan.Zero));

        await _coordinator.TickAsync(default);
        await Task.Delay(50);

        Assert.Empty(_runner.Vm);
        Assert.Equal(0, _runner.HostRuns);
    }

    private Hold Acquire()
    {
        Interlocked.Increment(ref _holds);
        return new Hold(() => Interlocked.Decrement(ref _holds));
    }

    private sealed class Hold(Action release) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                release();
            }
        }
    }

    private sealed class FakeSessions(Func<bool> active) : IVmSessionProbe
    {
        public Task<bool> HasActiveSessionAsync(Vm vm, CancellationToken cancellationToken) => Task.FromResult(active());
    }

    private sealed class RecordingRunner(BackupHistoryStore history) : IBackupRunner
    {
        private int _hostRuns;

        public List<VmBackupRequest> Vm { get; } = [];

        public int HostRuns => Volatile.Read(ref _hostRuns);

        public BackupOutcome Outcome { get; set; } = BackupOutcome.Succeeded;

        public Task<BackupRunRecord> RunVmAsync(VmBackupRequest request, Action<string, int> progress, CancellationToken cancellationToken)
        {
            lock (Vm)
            {
                Vm.Add(request);
            }

            return Task.FromResult(Record($"vm:{request.VmId:D}", request.Trigger));
        }

        public Task<BackupRunRecord> RunHostStateAsync(BackupTarget target, BackupTrigger trigger, Action<string, int> progress, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _hostRuns);
            return Task.FromResult(Record("host", trigger));
        }

        private BackupRunRecord Record(string item, BackupTrigger trigger)
        {
            var record = Build(item, trigger);
            history.Add(record);
            return record;
        }

        private BackupRunRecord Build(string item, BackupTrigger trigger) =>
            new(Guid.NewGuid(), item, null, null, "t1", trigger, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Outcome,
                Outcome == BackupOutcome.Failed ? "boom" : null, "s1", 1, []);
    }
}
