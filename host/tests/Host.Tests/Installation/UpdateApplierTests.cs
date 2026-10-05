using HyperHarbor.Host.Core.Installation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Installation;

/// <summary>
/// The update state machine on a real layout (junctions in a temporary folder) and real data backups, with
/// the service and its health faked. A "crash" is an OperationCanceledException from the fake service, which
/// escapes the applier like a killed process would; a new applier then recovers from the saved state.
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private static readonly SemanticVersion From = SemanticVersion.Parse("1.0.0");
    private static readonly SemanticVersion To = SemanticVersion.Parse("1.1.0");

    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly InstallLayout _layout;
    private readonly string _data;
    private readonly string _staged;
    private readonly UpdateStateStore _store;
    private readonly FakeService _service;
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-04T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public UpdateApplierTests()
    {
        _layout = new InstallLayout(Path.Combine(_root, "HyperHarbor"));
        _data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        File.WriteAllText(Path.Combine(_data, "paired-devices.json"), "devices v1");
        File.WriteAllText(Path.Combine(_data, "audit.log"), "audit 1\n");

        File.WriteAllText(Path.Combine(_root, "from.exe"), "version 1.0.0");
        _layout.Stage(Path.Combine(_root, "from.exe"), From);
        _layout.Activate(From);
        _staged = Path.Combine(_data, "update", "downloads", "HyperHarbor.Host-1.1.0.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_staged)!);
        File.WriteAllText(_staged, "version 1.1.0");

        _store = new UpdateStateStore(_data);
        _service = new FakeService(_layout, _data);
    }

    public void Dispose()
    {
        foreach (var link in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Where(Junction.IsJunction).ToList())
        {
            Junction.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task HealthyOnTheFirstStart_Commits()
    {
        HandOff();

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Equal("Updated from 1.0.0 to 1.1.0.", state.LastResult);
        Assert.Equal(To, _layout.CurrentVersion);
        Assert.Equal("version 1.1.0", File.ReadAllText(_layout.HelperExecutable));
        Assert.False(File.Exists(_staged));
        Assert.Equal([From, To], _layout.InstalledVersions);
        Assert.Single(Directory.EnumerateDirectories(DataBackup.BackupsFolder(_data)));
        Assert.Equal(["stop", "start 1.1.0"], _service.Calls);
        var saved = _store.Load();
        Assert.Equal((state.Phase, state.LastResult, state.From), (saved.Phase, saved.LastResult, saved.From));
    }

    [Fact]
    public async Task FailingOnceThenHealthy_CommitsOnTheSecondAttempt()
    {
        HandOff();
        _service.Healthy[To] = new Queue<bool>([false, true]);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal("Updated from 1.0.0 to 1.1.0.", state.LastResult);
        Assert.Equal(["stop", "start 1.1.0", "stop", "start 1.1.0"], _service.Calls);
    }

    [Fact]
    public async Task TwoFailedStarts_RollBack_RestoreTheData_AndKeepTheAuditTrail()
    {
        HandOff();
        _service.Healthy[To] = new Queue<bool>([false, false]);

        // The new version migrates the data and writes to the audit trail before it fails.
        _service.OnStart = version =>
        {
            if (version == To)
            {
                File.WriteAllText(Path.Combine(_data, "paired-devices.json"), "devices v2");
                File.WriteAllText(Path.Combine(_data, DataFormat.FileName), """{ "format": 2 }""");
                File.AppendAllText(Path.Combine(_data, "audit.log"), "audit 2\n");
            }
        };

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Equal("Version 1.1.0 did not start, so 1.0.0 was restored.", state.LastResult);
        Assert.Equal(["1.1.0"], state.RolledBack);
        Assert.Equal(From, _layout.CurrentVersion);
        Assert.Equal("devices v1", File.ReadAllText(Path.Combine(_data, "paired-devices.json")));
        Assert.False(File.Exists(Path.Combine(_data, DataFormat.FileName)));
        Assert.Equal("audit 1\naudit 2\naudit 2\n", File.ReadAllText(Path.Combine(_data, "audit.log")));
        Assert.Equal(["stop", "start 1.1.0", "stop", "start 1.1.0", "stop", "stop", "start 1.0.0"], _service.Calls);
        Assert.False(File.Exists(_staged));
        Assert.Equal([From], _layout.InstalledVersions);
    }

    [Fact]
    public async Task WhenThePreviousVersionAlsoFails_TheUpdateIsMarkedFailed()
    {
        HandOff();
        _service.Healthy[To] = new Queue<bool>([false, false]);
        _service.Healthy[From] = new Queue<bool>([false]);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Failed, state.Phase);
        Assert.Contains("Reinstall HyperHarbor", state.LastResult, StringComparison.Ordinal);
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task AServiceThatWillNotStop_KeepsTheRunningVersion()
    {
        HandOff();
        _service.StopFailures = 1;

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.StartsWith("The update to 1.1.0 failed before it started, so 1.0.0 was kept", state.LastResult, StringComparison.Ordinal);
        Assert.Equal(From, _layout.CurrentVersion);
        Assert.Equal(["stop", "start 1.0.0"], _service.Calls);
        Assert.False(Directory.Exists(DataBackup.BackupsFolder(_data)) && Directory.EnumerateDirectories(DataBackup.BackupsFolder(_data)).Any());
    }

    [Fact]
    public async Task AMissingStagedExecutable_KeepsTheRunningVersion()
    {
        HandOff();
        File.Delete(_staged);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.StartsWith("The update to 1.1.0 failed before it started", state.LastResult, StringComparison.Ordinal);
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task CrashWhileStopping_Recovery_KeepsTheRunningVersion()
    {
        HandOff();
        _service.CrashOn = "stop";

        await Assert.ThrowsAsync<OperationCanceledException>(() => Applier().RunAsync(CancellationToken.None));
        Assert.Equal(UpdatePhase.Stopping, _store.Load().Phase);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Equal("The update to 1.1.0 was interrupted before it started, so 1.0.0 was kept.", state.LastResult);
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task CrashWhileBackingUp_Recovery_RemovesThePartialBackup()
    {
        var backup = Path.Combine(DataBackup.BackupsFolder(_data), "partial");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "users.json"), "half");
        _store.Save(HandedOff() with { Phase = UpdatePhase.BackingUp, Backup = backup });

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.False(Directory.Exists(backup));
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task CrashAfterTheFlip_BeforeAnyStart_Recovery_PutsTheJunctionBack()
    {
        _layout.Stage(_staged, To);
        _layout.Activate(To);
        _store.Save(HandedOff() with { Phase = UpdatePhase.Flipping });

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(From, _layout.CurrentVersion);
        Assert.Equal(["start 1.0.0"], _service.Calls);
        Assert.Equal(UpdatePhase.Idle, state.Phase);
    }

    [Fact]
    public async Task CrashInTheMiddleOfTheJunctionSwap_Recovery_LeavesOneValidJunction()
    {
        // Repoint was between removing "current" and renaming "current.next" into place.
        _layout.Stage(_staged, To);
        Junction.Create(_layout.Current + ".next", _layout.VersionFolder(To));
        Junction.Delete(_layout.Current);
        _store.Save(HandedOff() with { Phase = UpdatePhase.Flipping });

        await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(From, _layout.CurrentVersion);
        Assert.False(Directory.Exists(_layout.Current + ".next"));
    }

    [Fact]
    public async Task CrashWhileStarting_WhenTheNewVersionCameUp_Recovery_Commits()
    {
        HandOff();
        _service.CrashOn = "start 1.1.0";

        await Assert.ThrowsAsync<OperationCanceledException>(() => Applier().RunAsync(CancellationToken.None));
        Assert.Equal(UpdatePhase.Starting, _store.Load().Phase);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal("Updated from 1.0.0 to 1.1.0.", state.LastResult);
        Assert.Equal(To, _layout.CurrentVersion);
    }

    [Fact]
    public async Task CrashWhileStarting_WhenTheNewVersionIsDown_Recovery_RollsBack()
    {
        HandOff();
        _service.CrashOn = "start 1.1.0";
        await Assert.ThrowsAsync<OperationCanceledException>(() => Applier().RunAsync(CancellationToken.None));
        _service.Healthy[To] = new Queue<bool>([false]);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal("Version 1.1.0 did not start, so 1.0.0 was restored.", state.LastResult);
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task CrashWhileRollingBack_Recovery_FinishesTheRollback()
    {
        HandOff();
        _service.Healthy[To] = new Queue<bool>([false, false]);
        _service.CrashOn = "start 1.0.0";
        await Assert.ThrowsAsync<OperationCanceledException>(() => Applier().RunAsync(CancellationToken.None));
        Assert.Equal(UpdatePhase.RollingBack, _store.Load().Phase);

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal("Version 1.1.0 did not start, so 1.0.0 was restored.", state.LastResult);
        Assert.Equal(["1.1.0"], state.RolledBack);
        Assert.Equal(From, _layout.CurrentVersion);
    }

    [Fact]
    public async Task AStateFileSavedWithAByteOrderMark_IsRead()
    {
        File.WriteAllText(_store.Path, $$"""{ "phase": "handingOff", "from": "1.0.0", "to": "1.1.0", "stagedExecutable": {{System.Text.Json.JsonSerializer.Serialize(_staged)}} }""", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal("Updated from 1.0.0 to 1.1.0.", state.LastResult);
    }

    [Fact]
    public async Task Idle_DoesNothing()
    {
        var state = await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdatePhase.Idle, state.Phase);
        Assert.Empty(_service.Calls);
    }

    [Fact]
    public async Task OnlyTheNewestBackupsAreKept()
    {
        for (var i = 0; i < 3; i++)
        {
            var old = Directory.CreateDirectory(Path.Combine(DataBackup.BackupsFolder(_data), $"old-{i}"));
            old.CreationTimeUtc = DateTime.UtcNow.AddDays(-10 + i);
        }

        HandOff();
        await Applier().RunAsync(CancellationToken.None);

        Assert.Equal(UpdateApplier.BackupsKept, Directory.EnumerateDirectories(DataBackup.BackupsFolder(_data)).Count());
    }

    private UpdateState HandedOff() => new()
    {
        Phase = UpdatePhase.HandingOff,
        From = From.ToString(),
        To = To.ToString(),
        StagedExecutable = _staged,
    };

    private void HandOff() => _store.Save(HandedOff());

    private UpdateApplier Applier() =>
        new(_layout, _data, _store, _service, _service, _time, NullLogger.Instance) { HealthTimeout = TimeSpan.FromSeconds(1) };

    /// <summary>The service and its health: "starting" records the version current points at.</summary>
    private sealed class FakeService(InstallLayout layout, string data) : IServiceControl, IHealthProbe
    {
        private SemanticVersion? _started;

        public List<string> Calls { get; } = [];

        public Dictionary<SemanticVersion, Queue<bool>> Healthy { get; } = [];

        public int StopFailures { get; set; }

        public string? CrashOn { get; set; }

        public Action<SemanticVersion>? OnStart { get; set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _started = layout.CurrentVersion ?? throw new InvalidOperationException("current points nowhere");
            Record($"start {_started}");
            OnStart?.Invoke(_started);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Record("stop");
            if (StopFailures > 0)
            {
                StopFailures--;
                throw new InvalidOperationException("The service did not stop.");
            }

            _started = null;
            return Task.CompletedTask;
        }

        public Task<bool> WaitHealthyAsync(SemanticVersion expected, DateTimeOffset since, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var healthy = layout.CurrentVersion == expected &&
                (!Healthy.TryGetValue(expected, out var outcomes) || outcomes.Count == 0 || outcomes.Dequeue());
            Assert.True(Directory.Exists(data));
            return Task.FromResult(healthy);
        }

        private void Record(string call)
        {
            Calls.Add(call);
            if (call == CrashOn)
            {
                CrashOn = null;
                throw new OperationCanceledException("The helper was killed.");
            }
        }
    }
}
