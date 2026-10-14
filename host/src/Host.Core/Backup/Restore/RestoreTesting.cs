using System.Globalization;
using System.Text.Json;
using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Core.Installation;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup.Restore;

public enum RestoreTestOutcome
{
    Passed,
    Failed,
    Skipped,
}

/// <summary>When the restore test runs. It is a test of the latest backups, so it defaults to on, monthly, in the small hours.</summary>
public sealed record RestoreTestSettings
{
    public const int MinimumIntervalDays = 1;
    public const int MaximumIntervalDays = 365;

    public bool Enabled { get; init; } = true;

    public int IntervalDays { get; init; } = 30;

    public TimeWindow Window { get; init; } = new(new TimeOnly(3, 0), new TimeOnly(6, 0));

    public IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        if (IntervalDays is < MinimumIntervalDays or > MaximumIntervalDays)
        {
            problems.Add($"The interval must be between {MinimumIntervalDays} and {MaximumIntervalDays} days.");
        }

        return problems;
    }
}

/// <summary>One VM's restore test. Holds no secrets.</summary>
public sealed record RestoreTestRecord(
    Guid RunId,
    Guid? VmId,
    string VmName,
    string TargetId,
    string? SnapshotId,
    BackupTrigger Trigger,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    RestoreTestOutcome Outcome,
    string? Reason,
    IReadOnlyList<string> Warnings);

/// <summary>Settings and results of the restore test (restore-test.json and restore-tests.json). Plain JSON, no secrets.</summary>
public sealed class RestoreTestStore
{
    public const string SettingsFileName = "restore-test.json";
    public const string ResultsFileName = "restore-tests.json";
    public const int MaxRecords = 200;

    private readonly string _settingsPath;
    private readonly string _resultsPath;
    private readonly object _gate = new();

    public RestoreTestStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _settingsPath = Path.Combine(dataDirectory, SettingsFileName);
        _resultsPath = Path.Combine(dataDirectory, ResultsFileName);
    }

    public RestoreTestSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return File.Exists(_settingsPath)
                    ? JsonSerializer.Deserialize<RestoreTestSettings>(Utf8Json.ReadFile(_settingsPath), BackupJson.Options) ?? new RestoreTestSettings()
                    : new RestoreTestSettings();
            }
        }
    }

    public void SaveSettings(RestoreTestSettings settings)
    {
        lock (_gate)
        {
            AtomicJson.Write(_settingsPath, settings, BackupJson.Options);
        }
    }

    public void Add(RestoreTestRecord record)
    {
        lock (_gate)
        {
            var all = Read();
            all.Add(record);
            if (all.Count > MaxRecords)
            {
                all.RemoveRange(0, all.Count - MaxRecords);
            }

            AtomicJson.Write(_resultsPath, all, BackupJson.Options);
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RestoreTestRecord> List(int limit = 50)
    {
        lock (_gate)
        {
            return Read().AsEnumerable().Reverse().Take(limit).ToList();
        }
    }

    /// <summary>When the most recent test started, or null when none ran.</summary>
    public DateTimeOffset? LastStartedAt()
    {
        lock (_gate)
        {
            var all = Read();
            return all.Count == 0 ? null : all.Max(r => r.StartedAt);
        }
    }

    private List<RestoreTestRecord> Read() =>
        File.Exists(_resultsPath)
            ? JsonSerializer.Deserialize<List<RestoreTestRecord>>(Utf8Json.ReadFile(_resultsPath), BackupJson.Options) ?? []
            : [];
}

/// <summary>The latest good backup of one VM: what the restore test restores.</summary>
public sealed record RestoreTestCandidate(Guid VmId, string VmName, BackupTarget Target, string SnapshotId, long TotalBytes);

/// <summary>Restores one VM from a backup; the seam between the test and <see cref="RestoreCoordinator"/>.</summary>
public interface IVmRestorer
{
    Task<RestoreVmResult> RestoreAsync(RestoreVmRequest request, Action<string, int> progress, CancellationToken cancellationToken);
}

public sealed class CoordinatorVmRestorer(RestoreCoordinator coordinator) : IVmRestorer
{
    public Task<RestoreVmResult> RestoreAsync(RestoreVmRequest request, Action<string, int> progress, CancellationToken cancellationToken) =>
        coordinator.RestoreAsync(request, progress, cancellationToken);
}

/// <summary>Picks what to test and when. Pure.</summary>
public static class RestoreTestPlanner
{
    /// <summary>
    /// For each VM, its newest successful backup on an enabled target whose recovery key is confirmed. A VM's name is the
    /// one it had at that backup.
    /// </summary>
    public static IReadOnlyList<RestoreTestCandidate> Candidates(IReadOnlyList<BackupTarget> targets, IEnumerable<BackupRunRecord> history)
    {
        var usable = targets
            .Where(t => t.Enabled && t.VmRepository?.KeyState == RepositoryKeyState.Confirmed)
            .ToDictionary(t => t.Id, StringComparer.Ordinal);
        return history
            .Where(r => r is { VmId: not null, SnapshotId: not null, Outcome: BackupOutcome.Succeeded or BackupOutcome.Partial } && usable.ContainsKey(r.TargetId))
            .GroupBy(r => r.VmId!.Value)
            .Select(g => g.OrderByDescending(r => r.FinishedAt).First())
            .Select(r => new RestoreTestCandidate(r.VmId!.Value, r.VmName ?? r.VmId.Value.ToString("D"), usable[r.TargetId], r.SnapshotId!, r.TotalBytes))
            .OrderBy(c => c.VmName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The moment the next scheduled test is due, or null when the test is off.</summary>
    public static DateTimeOffset? NextDue(RestoreTestSettings settings, DateTimeOffset? lastStarted, DateTimeOffset now) =>
        !settings.Enabled ? null : lastStarted is { } last ? last.AddDays(settings.IntervalDays) : now;

    /// <summary>Space a restore of <paramref name="bytes"/> needs: the files, a little slack, and room for the guest to boot.</summary>
    public static long RequiredSpace(long bytes) => bytes + (bytes / 10) + (1L << 30);
}

/// <summary>
/// The monthly restore test: restores the latest backup of each VM as a throwaway VM without a network, starts it, waits for the
/// guest to report in, records the result, and always removes the throwaway VM and its files. A backup that cannot be restored
/// is found now, not on the day it is needed.
/// </summary>
public sealed class RestoreTestService(
    RestoreTestStore store,
    BackupConfigStore config,
    BackupHistoryStore history,
    IVmRestorer restorer,
    IHyperVPowerInvoker power,
    IHyperVStorage storage,
    Func<Guid, bool> hasGpuPartition,
    Func<bool> gameActive,
    Func<bool> backupBusy,
    Func<IDisposable?> beginActivity,
    Func<CancellationToken, Task<string>> destinationRoot,
    Func<string, long> freeSpace,
    TimeProvider time,
    ILogger<RestoreTestService> logger) : IDisposable
{
    public const string NamePrefix = "HH-RestoreTest-";
    public const int RemoveAttempts = 10;

    /// <summary>Pause between attempts to remove the throwaway VM.</summary>
    public TimeSpan RemoveRetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private bool _running;
    private string? _current;
    private Task _background = Task.CompletedTask;

    public bool Running
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>The VM being tested, while a test runs.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public IReadOnlyList<RestoreTestCandidate> Candidates() => RestoreTestPlanner.Candidates(config.Load().Targets, history.List(BackupHistoryStore.MaxRecords));

    public DateTimeOffset? NextDue() => RestoreTestPlanner.NextDue(store.Settings, store.LastStartedAt(), time.GetUtcNow());

    /// <summary>Starts a scheduled test when one is due, the time of day allows it, and the host is quiet.</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var settings = store.Settings;
        if (!settings.Enabled || Running)
        {
            return;
        }

        if (NextDue() is not { } due || due > time.GetUtcNow())
        {
            return;
        }

        if (!RunConditionEvaluator.InWindow(settings.Window, TimeOnly.FromDateTime(time.GetLocalNow().DateTime)) || gameActive() || backupBusy())
        {
            return;
        }

        if (Candidates().Count == 0)
        {
            return;
        }

        await RunAsync(BackupTrigger.Schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a test now in the background.</summary>
    /// <exception cref="BackupException">A test is already running, or no VM has a backup to test.</exception>
    public void StartManual()
    {
        if (Candidates().Count == 0)
        {
            throw new BackupException("No virtual machine has a backup to test yet.");
        }

        lock (_gate)
        {
            if (_running)
            {
                throw new BackupException("A restore test is already running.");
            }

            _running = true;
        }

        _background = Task.Run(() => RunCoreAsync(BackupTrigger.Manual, _stopping.Token));
    }

    /// <summary>Tests every VM with a backup, one after the other.</summary>
    public async Task RunAsync(BackupTrigger trigger, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_running)
            {
                throw new BackupException("A restore test is already running.");
            }

            _running = true;
        }

        await RunCoreAsync(trigger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Waits for a background test to end (for tests and for shutdown).</summary>
    public Task WhenIdle() => _background;

    private async Task RunCoreAsync(BackupTrigger trigger, CancellationToken cancellationToken)
    {
        IDisposable? hold = null;
        try
        {
            hold = beginActivity();
            if (hold is null)
            {
                logger.LogInformation("The restore test waits: the host is about to update.");
                return;
            }

            var root = await destinationRoot(cancellationToken).ConfigureAwait(false);
            foreach (var candidate in Candidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    _current = candidate.VmName;
                }

                var record = gameActive()
                    ? Skipped(candidate, trigger, "A game started on the host, so the test did not run.")
                    : await TestAsync(candidate, trigger, root, cancellationToken).ConfigureAwait(false);
                store.Add(record);
                logger.LogInformation("Restore test of {Vm}: {Outcome} {Reason}", candidate.VmName, record.Outcome, record.Reason);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("The restore test was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The restore test stopped.");
        }
        finally
        {
            hold?.Dispose();
            lock (_gate)
            {
                _running = false;
                _current = null;
            }
        }
    }

    private async Task<RestoreTestRecord> TestAsync(RestoreTestCandidate candidate, BackupTrigger trigger, string root, CancellationToken cancellationToken)
    {
        if (hasGpuPartition(candidate.VmId))
        {
            return Skipped(candidate, trigger, "The VM has a GPU partition. Starting a copy would take the GPU from the host.");
        }

        var free = FreeSpaceOrNull(root);
        if (free is { } available && available < RestoreTestPlanner.RequiredSpace(candidate.TotalBytes))
        {
            return Skipped(
                candidate, trigger,
                $"Not enough free space in {root}: {Gigabytes(RestoreTestPlanner.RequiredSpace(candidate.TotalBytes))} GB are needed and {Gigabytes(available)} GB are free.");
        }

        var started = time.GetUtcNow();
        var name = NamePrefix + Guid.NewGuid().ToString("N")[..8];
        RestoreVmResult? result = null;
        string? reason = null;
        var warnings = new List<string>();
        try
        {
            result = await restorer.RestoreAsync(
                new RestoreVmRequest(candidate.Target, candidate.SnapshotId, root, name, Start: true, Administrator: null, Isolated: true),
                (_, _) => { },
                cancellationToken).ConfigureAwait(false);
            warnings.AddRange(result.Warnings);
            if (!result.GuestReported)
            {
                reason = result.Outcome == RestoreOutcome.NeedsConsole
                    ? "The restored VM started, but the guest did not report in within 5 minutes."
                    : result.Warnings.FirstOrDefault(w => w.Contains("could not be started", StringComparison.Ordinal)) ?? "The restored VM did not start.";
            }
        }
        catch (RestoreException ex)
        {
            reason = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The restore test of {Vm} failed unexpectedly.", candidate.VmName);
            reason = "The restore test failed unexpectedly. The host log has the details.";
        }
        finally
        {
            if (result is not null && await RemoveAsync(result, root).ConfigureAwait(false) is { } problem)
            {
                warnings.Add($"The test VM \"{result.VmName}\" could not be removed ({problem}). Delete it and its folder by hand.");
            }
        }

        return new RestoreTestRecord(
            Guid.NewGuid(), candidate.VmId, candidate.VmName, candidate.Target.Id, candidate.SnapshotId, trigger,
            started, time.GetUtcNow(), reason is null ? RestoreTestOutcome.Passed : RestoreTestOutcome.Failed, reason, warnings);
    }

    /// <summary>Turns the throwaway VM off, removes it, and deletes its files. Only a VM this test created, by its name.</summary>
    /// <returns>Null when everything was removed, else what went wrong.</returns>
    private async Task<string?> RemoveAsync(RestoreVmResult result, string root)
    {
        if (!result.VmName.StartsWith(NamePrefix, StringComparison.Ordinal))
        {
            logger.LogError("Refusing to remove {Name}: it is not a restore test VM.", result.VmName);
            return "the name is not a restore test name";
        }

        string? problem = null;
        try
        {
            await power.InvokeAsync(result.VmId, VmAction.TurnOff, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VmActionNotAllowedException or HyperVOperationException)
        {
            // Already off, or never started; deleting it is still the goal.
            logger.LogDebug(ex, "Turning off the test VM {Name} was not needed.", result.VmName);
        }

        // Turning off can return before Hyper-V reports the VM as off, and a VM that is not off cannot be removed.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await storage.DeleteVmAsync(result.VmId, CancellationToken.None).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (attempt >= RemoveAttempts)
                {
                    logger.LogError(ex, "The test VM {Name} could not be removed.", result.VmName);
                    problem = ex.Message;
                    break;
                }

                await Task.Delay(RemoveRetryDelay, time, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (problem is null)
        {
            try
            {
                var folder = Path.Combine(root, result.VmName);
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogError(ex, "The files of the test VM {Name} could not be removed.", result.VmName);
                problem = ex.Message;
            }
        }

        return problem;
    }

    private long? FreeSpaceOrNull(string root)
    {
        try
        {
            return freeSpace(root);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private RestoreTestRecord Skipped(RestoreTestCandidate candidate, BackupTrigger trigger, string reason)
    {
        var now = time.GetUtcNow();
        return new RestoreTestRecord(Guid.NewGuid(), candidate.VmId, candidate.VmName, candidate.Target.Id, candidate.SnapshotId, trigger, now, now, RestoreTestOutcome.Skipped, reason, []);
    }

    private static string Gigabytes(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
