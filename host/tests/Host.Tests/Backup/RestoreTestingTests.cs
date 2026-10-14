using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using HyperHarbor.Host.Tests.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class RestoreTestingTests : IDisposable
{
    private static readonly Guid VmA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly Guid VmB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000000b");
    private readonly string _data = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _root;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 3, 30, 0, TimeSpan.Zero));
    private readonly RestoreTestStore _store;
    private readonly BackupConfigStore _config;
    private readonly BackupHistoryStore _history;
    private readonly ScriptedRestorer _restorer = new();
    private readonly FakePowerInvoker _power = new();
    private readonly FakeHyperVStorage _storage = new();
    private readonly HashSet<Guid> _gpu = [];
    private readonly RestoreTestService _service;
    private bool _game;
    private bool _backupBusy;
    private bool _gateClosed;
    private int _holds;
    private long _free = 500L << 30;

    public RestoreTestingTests()
    {
        _root = Directory.CreateDirectory(Path.Combine(_data, "vms")).FullName;
        _store = new RestoreTestStore(_data);
        _config = new BackupConfigStore(_data);
        _history = new BackupHistoryStore(_data);
        _config.Update(_ => new BackupConfig { Targets = [Target("t1")] });
        _restorer.Root = _root;
        _service = new RestoreTestService(
            _store, _config, _history, _restorer, _power, _storage,
            id => _gpu.Contains(id), () => _game, () => _backupBusy,
            () => _gateClosed ? null : new Hold(this),
            _ => Task.FromResult(_root), _ => _free, _time, NullLogger<RestoreTestService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        Directory.Delete(_data, recursive: true);
    }

    private static BackupTarget Target(string id, bool enabled = true, RepositoryKeyState key = RepositoryKeyState.Confirmed) => new()
    {
        Id = id, Name = id, Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B"), Enabled = enabled,
        VmRepository = new RepositoryRef("r-" + id, key), HostRepository = new RepositoryRef("h-" + id, key),
    };

    private BackupRunRecord Backup(Guid vm, string name, string snapshot, BackupOutcome outcome = BackupOutcome.Succeeded, string target = "t1", int hoursAgo = 1, long bytes = 10L << 30) =>
        new(Guid.NewGuid(), $"vm:{vm:D}", vm, name, target, BackupTrigger.Schedule, _time.GetUtcNow().AddHours(-hoursAgo), _time.GetUtcNow().AddHours(-hoursAgo),
            outcome, null, snapshot, bytes, []);

    private void Seed(params BackupRunRecord[] records)
    {
        foreach (var record in records)
        {
            _history.Add(record);
        }
    }

    [Fact]
    public void Candidates_AreTheNewestGoodBackupPerVm_OnUsableTargets()
    {
        var targets = new[] { Target("t1"), Target("off", enabled: false), Target("pending", key: RepositoryKeyState.PendingKey) };
        var history = new[]
        {
            Backup(VmA, "Alpha", "old", hoursAgo: 30),
            Backup(VmA, "Alpha", "new", hoursAgo: 5),
            Backup(VmA, "Alpha", "failed", BackupOutcome.Failed, hoursAgo: 1),
            Backup(VmB, "Beta", "on-disabled", target: "off"),
            Backup(Guid.NewGuid(), "Gamma", "on-pending", target: "pending"),
        };

        var candidates = RestoreTestPlanner.Candidates(targets, history);

        var only = Assert.Single(candidates);
        Assert.Equal("new", only.SnapshotId);
        Assert.Equal("Alpha", only.VmName);
    }

    [Fact]
    public void Candidates_IgnoreHostStateAndRunsWithoutASnapshot()
    {
        var host = new BackupRunRecord(Guid.NewGuid(), "host", null, null, "t1", BackupTrigger.Schedule, _time.GetUtcNow(), _time.GetUtcNow(), BackupOutcome.Succeeded, null, "h", 1, []);
        var skipped = Backup(VmA, "Alpha", "x", BackupOutcome.Skipped);

        Assert.Empty(RestoreTestPlanner.Candidates([Target("t1")], [host, skipped with { SnapshotId = null }]));
    }

    [Fact]
    public void NextDue_IsNowWhenNeverTested_ThenOneIntervalAfterTheLastStart_AndNullWhenOff()
    {
        var now = _time.GetUtcNow();
        var settings = new RestoreTestSettings { IntervalDays = 30 };

        Assert.Equal(now, RestoreTestPlanner.NextDue(settings, null, now));
        Assert.Equal(now.AddDays(25), RestoreTestPlanner.NextDue(settings, now.AddDays(-5), now));
        Assert.Null(RestoreTestPlanner.NextDue(settings with { Enabled = false }, null, now));
    }

    [Fact]
    public void Settings_RejectAnImpossibleInterval()
    {
        Assert.Empty(new RestoreTestSettings().Validate());
        Assert.Single(new RestoreTestSettings { IntervalDays = 0 }.Validate());
        Assert.Single(new RestoreTestSettings { IntervalDays = 366 }.Validate());
    }

    [Fact]
    public void Store_RoundTripsSettingsAndKeepsNewestFirst()
    {
        _store.SaveSettings(new RestoreTestSettings { Enabled = false, IntervalDays = 14, Window = new TimeWindow(new TimeOnly(1, 0), new TimeOnly(2, 30)) });
        var reloaded = new RestoreTestStore(_data).Settings;

        Assert.False(reloaded.Enabled);
        Assert.Equal(14, reloaded.IntervalDays);
        Assert.Equal(new TimeOnly(2, 30), reloaded.Window.End);

        var start = _time.GetUtcNow();
        for (var i = 0; i < RestoreTestStore.MaxRecords + 5; i++)
        {
            _store.Add(new RestoreTestRecord(Guid.NewGuid(), VmA, $"n{i}", "t1", "s", BackupTrigger.Manual, start.AddMinutes(i), start.AddMinutes(i), RestoreTestOutcome.Passed, null, []));
        }

        Assert.Equal($"n{RestoreTestStore.MaxRecords + 4}", _store.List(1)[0].VmName);
        Assert.Equal(RestoreTestStore.MaxRecords, _store.List(1000).Count);
    }

    [Fact]
    public async Task APassingTest_RestoresIsolatedAndStarted_RecordsIt_AndRemovesTheThrowawayVm()
    {
        Seed(Backup(VmA, "Alpha", "snap-1", bytes: 8L << 30));

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        var request = Assert.Single(_restorer.Requests);
        Assert.True(request.Isolated);
        Assert.True(request.Start);
        Assert.Null(request.Administrator);
        Assert.Equal("snap-1", request.SnapshotId);
        Assert.StartsWith(RestoreTestService.NamePrefix, request.Name);
        var record = Assert.Single(_store.List());
        Assert.Equal(RestoreTestOutcome.Passed, record.Outcome);
        Assert.Equal("Alpha", record.VmName);
        Assert.Contains(_restorer.Created.Single(), _storage.DeletedVms);
        Assert.Contains(_power.Calls, call => call.Action == VmAction.TurnOff && call.VmId == _restorer.Created.Single());
        Assert.False(Directory.Exists(Path.Combine(_root, request.Name!)));
        Assert.False(_service.Running);
        Assert.Equal(0, _holds);
    }

    [Fact]
    public async Task AGuestThatNeverReports_Fails_WithTheReason_AndStillCleansUp()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _restorer.GuestReports = false;

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        var record = Assert.Single(_store.List());
        Assert.Equal(RestoreTestOutcome.Failed, record.Outcome);
        Assert.Contains("did not report in", record.Reason);
        Assert.Single(_storage.DeletedVms);
        Assert.False(Directory.Exists(Path.Combine(_root, _restorer.Requests[0].Name!)));
    }

    [Fact]
    public async Task ARestoreThatFails_IsRecordedAsFailed_WithItsMessage()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _restorer.Failure = new RestoreException("The backup is missing the disk disk-0.vhdx.");

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        var record = Assert.Single(_store.List());
        Assert.Equal(RestoreTestOutcome.Failed, record.Outcome);
        Assert.Equal("The backup is missing the disk disk-0.vhdx.", record.Reason);
        Assert.Empty(_storage.DeletedVms);
    }

    [Fact]
    public async Task AVmThatCouldNotBeStarted_Fails_WithTheStartMessage()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _restorer.GuestReports = false;
        _restorer.Outcome = RestoreOutcome.SucceededWithWarnings;
        _restorer.ExtraWarnings.Add("The VM was restored but could not be started: not enough memory.");

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        Assert.Contains("could not be started", Assert.Single(_store.List()).Reason);
    }

    [Fact]
    public async Task AGpuVm_IsSkipped_WithoutRestoring()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _gpu.Add(VmA);

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        Assert.Empty(_restorer.Requests);
        var record = Assert.Single(_store.List());
        Assert.Equal(RestoreTestOutcome.Skipped, record.Outcome);
        Assert.Contains("GPU partition", record.Reason);
    }

    [Fact]
    public async Task NotEnoughFreeSpace_Skips_WithoutRestoring()
    {
        Seed(Backup(VmA, "Alpha", "snap-1", bytes: 100L << 30));
        _free = 50L << 30;

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        Assert.Empty(_restorer.Requests);
        Assert.Contains("free space", Assert.Single(_store.List()).Reason);
    }

    [Fact]
    public async Task AGameStartingMidRun_SkipsTheRest_AndTheFirstVmKeepsItsResult()
    {
        Seed(Backup(VmA, "Alpha", "a"), Backup(VmB, "Beta", "b"));
        _restorer.AfterRestore = () => _game = true;

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        var records = _store.List().OrderBy(r => r.VmName).ToList();
        Assert.Equal(RestoreTestOutcome.Passed, records[0].Outcome);
        Assert.Equal(RestoreTestOutcome.Skipped, records[1].Outcome);
        Assert.Contains("game", records[1].Reason);
        Assert.Single(_restorer.Requests);
    }

    [Fact]
    public async Task AThrowawayVmThatCannotBeDeleted_StillPasses_ButWarnsToDeleteItByHand()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _storage.ThrowOnDeleteVm = new InvalidOperationException("locked");

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        var record = Assert.Single(_store.List());
        Assert.Equal(RestoreTestOutcome.Passed, record.Outcome);
        Assert.Contains(record.Warnings, w => w.Contains("Delete it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ANameThatIsNotATestVm_IsNeverRemoved()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _restorer.ResultName = "Production VM";

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        Assert.Empty(_storage.DeletedVms);
        Assert.Contains(Assert.Single(_store.List()).Warnings, w => w.Contains("Delete it", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClosedUpdateGate_RunsNothing()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        _gateClosed = true;

        await _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);

        Assert.Empty(_restorer.Requests);
        Assert.Empty(_store.List());
        Assert.False(_service.Running);
    }

    [Fact]
    public async Task ASecondRun_WhileOneRuns_IsRefused()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));
        var gate = new TaskCompletionSource();
        _restorer.Block = gate.Task;

        var first = _service.RunAsync(BackupTrigger.Manual, CancellationToken.None);
        await _restorer.Entered.Task;

        Assert.True(_service.Running);
        Assert.Equal("Alpha", _service.Current);
        await Assert.ThrowsAsync<BackupException>(() => _service.RunAsync(BackupTrigger.Manual, CancellationToken.None));
        Assert.Throws<BackupException>(() => _service.StartManual());

        gate.SetResult();
        await first;
        Assert.False(_service.Running);
        Assert.Null(_service.Current);
    }

    [Fact]
    public void StartManual_WithoutAnyBackup_Says_SoPlainly()
    {
        var ex = Assert.Throws<BackupException>(() => _service.StartManual());
        Assert.Contains("No virtual machine has a backup", ex.Message);
    }

    [Fact]
    public async Task StartManual_RunsInTheBackground()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));

        _service.StartManual();
        await _service.WhenIdle();

        Assert.Equal(RestoreTestOutcome.Passed, Assert.Single(_store.List()).Outcome);
        Assert.Equal(BackupTrigger.Manual, _store.List()[0].Trigger);
    }

    [Fact]
    public async Task Tick_RunsWhenDueInsideTheWindow_ThenWaitsAnInterval()
    {
        Seed(Backup(VmA, "Alpha", "snap-1"));

        await _service.TickAsync(CancellationToken.None);
        Assert.Single(_restorer.Requests);
        Assert.Equal(BackupTrigger.Schedule, _store.List()[0].Trigger);

        await _service.TickAsync(CancellationToken.None);
        Assert.Single(_restorer.Requests);

        _time.Advance(TimeSpan.FromDays(31));
        _time.SetUtcNow(new DateTimeOffset(2026, 11, 12, 4, 0, 0, TimeSpan.Zero));
        await _service.TickAsync(CancellationToken.None);
        Assert.Equal(2, _restorer.Requests.Count);
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("outsideWindow")]
    [InlineData("game")]
    [InlineData("backupRunning")]
    [InlineData("noBackups")]
    public async Task Tick_WaitsWhen(string reason)
    {
        if (reason != "noBackups")
        {
            Seed(Backup(VmA, "Alpha", "snap-1"));
        }

        switch (reason)
        {
            case "disabled": _store.SaveSettings(new RestoreTestSettings { Enabled = false }); break;
            case "outsideWindow": _time.SetUtcNow(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero)); break;
            case "game": _game = true; break;
            case "backupRunning": _backupBusy = true; break;
        }

        await _service.TickAsync(CancellationToken.None);

        Assert.Empty(_restorer.Requests);
        Assert.Empty(_store.List());
    }

    private sealed class Hold : IDisposable
    {
        private readonly RestoreTestingTests _owner;

        public Hold(RestoreTestingTests owner)
        {
            _owner = owner;
            _owner._holds++;
        }

        public void Dispose() => _owner._holds--;
    }

    /// <summary>Stands in for the restore engine: creates the files a real restore would, and reports what the test configures.</summary>
    private sealed class ScriptedRestorer : IVmRestorer
    {
        public List<RestoreVmRequest> Requests { get; } = [];

        public List<Guid> Created { get; } = [];

        public string Root { get; set; } = string.Empty;

        public bool GuestReports { get; set; } = true;

        public RestoreOutcome Outcome { get; set; } = RestoreOutcome.Succeeded;

        public List<string> ExtraWarnings { get; } = [];

        public Exception? Failure { get; set; }

        public string? ResultName { get; set; }

        public Action? AfterRestore { get; set; }

        public Task? Block { get; set; }

        public TaskCompletionSource Entered { get; } = new();

        public async Task<RestoreVmResult> RestoreAsync(RestoreVmRequest request, Action<string, int> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Entered.TrySetResult();
            if (Block is not null)
            {
                await Block;
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            var id = Guid.NewGuid();
            Created.Add(id);
            var name = ResultName ?? request.Name!;
            Directory.CreateDirectory(Path.Combine(Root, request.Name!));
            AfterRestore?.Invoke();
            var outcome = GuestReports ? Outcome : (ExtraWarnings.Count > 0 ? Outcome : RestoreOutcome.NeedsConsole);
            return new RestoreVmResult(outcome, id, name, ExtraWarnings.ToList(), false, false, GuestReports);
        }
    }
}
