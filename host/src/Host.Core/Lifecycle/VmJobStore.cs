using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>A point-in-time copy of a job, safe to return to callers.</summary>
/// <param name="Kind">The operation.</param>
/// <param name="VmId">Null until a create job has defined its virtual machine.</param>
/// <param name="UserId">Only this User's devices can see the job.</param>
/// <param name="Step">What the job is doing now, for example "Deleting disks".</param>
/// <param name="ErrorTitle">Set when <paramref name="State"/> is Failed.</param>
/// <param name="ErrorDetail">Safe to show to the client; unexpected failures get a generic message.</param>
/// <param name="SetupResult">What an applySetupProfile job did.</param>
public sealed record VmJobSnapshot(
    Guid Id,
    VmJobKind Kind,
    Guid? VmId,
    Guid UserId,
    VmJobState State,
    string Step,
    int PercentComplete,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ErrorTitle,
    string? ErrorDetail,
    Shared.Contracts.Unattend.SetupProfileResult? SetupResult = null);

/// <summary>Handed to a job's work so it can report progress.</summary>
public sealed class VmJobContext
{
    private readonly VmJobStore _store;
    private readonly Guid _jobId;

    internal VmJobContext(VmJobStore store, Guid jobId, CancellationToken stopping)
    {
        _store = store;
        _jobId = jobId;
        Stopping = stopping;
    }

    /// <summary>Signalled when the host service is stopping.</summary>
    public CancellationToken Stopping { get; }

    public Guid JobId => _jobId;

    /// <param name="percentComplete">Clamped to 0 through 99; 100 is reported when the job succeeds.</param>
    public void Report(string step, int percentComplete) => _store.Update(_jobId, step, Math.Clamp(percentComplete, 0, 99));

    /// <summary>
    /// Records the virtual machine a create job has just defined and takes its lock for the rest of the job.
    /// </summary>
    /// <exception cref="InvalidOperationException">The job already has a virtual machine.</exception>
    public void AttachVm(Guid vmId) => _store.Attach(_jobId, vmId);

    /// <summary>Records what an applySetupProfile job did, for the job's snapshot.</summary>
    public void ReportSetupResult(Shared.Contracts.Unattend.SetupProfileResult result) => _store.SetSetupResult(_jobId, result);
}

/// <summary>
/// Runs long VM operations in the background and keeps their progress in memory so clients can poll
/// them. A job holds its virtual machine's <see cref="VmOperationLocks"/> lock until it ends, so only
/// one job (or power action) runs per VM. Finished jobs are kept for <see cref="Retention"/>, up to
/// <see cref="MaxFinishedJobs"/>; a service restart forgets them.
/// </summary>
public sealed class VmJobStore : IDisposable
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromHours(1);
    public const int DefaultMaxFinishedJobs = 100;
    private const string UnexpectedFailure = "The operation failed unexpectedly. The host log has details.";

    private readonly VmOperationLocks _locks;
    private readonly TimeProvider _time;
    private readonly ILogger<VmJobStore> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _jobs = [];

    public VmJobStore(
        VmOperationLocks locks,
        TimeProvider time,
        ILogger<VmJobStore> logger,
        TimeSpan? retention = null,
        int maxFinishedJobs = DefaultMaxFinishedJobs)
    {
        _locks = locks;
        _time = time;
        _logger = logger;
        Retention = retention ?? DefaultRetention;
        MaxFinishedJobs = maxFinishedJobs;
    }

    public TimeSpan Retention { get; }

    public int MaxFinishedJobs { get; }

    /// <summary>
    /// Takes the VM's lock (when <paramref name="vmId"/> is given) and starts <paramref name="work"/> in the
    /// background. The lock is taken before this returns, so a conflict is reported to the caller directly.
    /// </summary>
    /// <param name="onFinished">Called once with the final snapshot, for example to write the audit entry.</param>
    /// <exception cref="VmBusyException">Another job or power action holds the virtual machine.</exception>
    public VmJobSnapshot Start(
        VmJobKind kind,
        Guid? vmId,
        Guid userId,
        string firstStep,
        Func<VmJobContext, Task> work,
        Action<VmJobSnapshot>? onFinished = null)
    {
        var hold = vmId is { } id ? _locks.Acquire(id, Describe(kind)) : null;
        var now = _time.GetUtcNow();
        var entry = new Entry(new VmJobSnapshot(Guid.NewGuid(), kind, vmId, userId, VmJobState.Running, firstStep, 0, now, now, null, null));
        if (hold is not null)
        {
            entry.Holds.Add(hold);
        }

        lock (_gate)
        {
            Prune(now);
            _jobs[entry.Snapshot.Id] = entry;
        }

        var context = new VmJobContext(this, entry.Snapshot.Id, _stopping.Token);
        entry.Completion = Task.Run(() => RunAsync(entry, context, work, onFinished));
        return entry.Snapshot;
    }

    /// <summary>Whether any job is still running (a host update waits for none).</summary>
    public bool AnyRunning
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Values.Any(entry => entry.Snapshot.State == VmJobState.Running);
            }
        }
    }

    /// <summary>The job, or null when it does not exist, has been pruned, or belongs to another User.</summary>
    public VmJobSnapshot? Get(Guid jobId, Guid userId)
    {
        lock (_gate)
        {
            Prune(_time.GetUtcNow());
            return _jobs.TryGetValue(jobId, out var entry) && entry.Snapshot.UserId == userId ? entry.Snapshot : null;
        }
    }

    /// <summary>Completes when the job has finished. For tests.</summary>
    internal Task WhenFinished(Guid jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var entry) ? entry.Completion ?? Task.CompletedTask : Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }

    /// <summary>How a job is named to a caller that finds its VM busy.</summary>
    public static string Describe(VmJobKind kind) => kind switch
    {
        VmJobKind.CreateVm => "creating the virtual machine",
        VmJobKind.DeleteVm => "deleting the virtual machine",
        VmJobKind.ApplyCompute => "applying settings",
        VmJobKind.ApplyPerformance => "applying Performance mode",
        VmJobKind.PerformanceGuestSetup => "setting up the guest for Performance mode",
        VmJobKind.ExportDisks => "exporting the disks",
        VmJobKind.ApplySetupProfile => "applying a setup profile",
        _ => kind.ToString(),
    };

    internal void Update(Guid jobId, string step, int percentComplete)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var entry) && entry.Snapshot.State == VmJobState.Running)
            {
                entry.Snapshot = entry.Snapshot with { Step = step, PercentComplete = percentComplete, UpdatedAt = _time.GetUtcNow() };
            }
        }
    }

    internal void SetSetupResult(Guid jobId, Shared.Contracts.Unattend.SetupProfileResult result)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var entry))
            {
                entry.Snapshot = entry.Snapshot with { SetupResult = result, UpdatedAt = _time.GetUtcNow() };
            }
        }
    }

    internal void Attach(Guid jobId, Guid vmId)
    {
        lock (_gate)
        {
            var entry = _jobs[jobId];
            if (entry.Snapshot.VmId is not null)
            {
                throw new InvalidOperationException("The job already has a virtual machine.");
            }

            entry.Holds.Add(_locks.Acquire(vmId, Describe(entry.Snapshot.Kind)));
            entry.Snapshot = entry.Snapshot with { VmId = vmId, UpdatedAt = _time.GetUtcNow() };
        }
    }

    private async Task RunAsync(Entry entry, VmJobContext context, Func<VmJobContext, Task> work, Action<VmJobSnapshot>? onFinished)
    {
        VmJobSnapshot final;
        try
        {
            await work(context).ConfigureAwait(false);
            final = Finish(entry, snapshot => snapshot with { State = VmJobState.Succeeded, PercentComplete = 100 });
        }
        catch (Exception ex)
        {
            // HyperHarbor exceptions carry messages written for the client, and file errors name the file and
            // the problem (for example access denied); anything else may not be meant for the client.
            var expected = ex.GetType().Namespace?.StartsWith("HyperHarbor.", StringComparison.Ordinal) == true
                || ex is IOException or UnauthorizedAccessException;
            if (expected)
            {
                _logger.LogWarning("Job {JobId} ({Kind}) failed: {Message}", entry.Snapshot.Id, entry.Snapshot.Kind, ex.Message);
            }
            else
            {
                _logger.LogError(ex, "Job {JobId} ({Kind}) failed unexpectedly.", entry.Snapshot.Id, entry.Snapshot.Kind);
            }

            final = Finish(entry, snapshot => snapshot with
            {
                State = VmJobState.Failed,
                ErrorTitle = $"{snapshot.Step} failed",
                ErrorDetail = expected ? ex.Message : UnexpectedFailure,
            });
        }
        finally
        {
            foreach (var hold in entry.Holds)
            {
                hold.Dispose();
            }
        }

        try
        {
            onFinished?.Invoke(final);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The completion callback of job {JobId} failed.", final.Id);
        }
    }

    private VmJobSnapshot Finish(Entry entry, Func<VmJobSnapshot, VmJobSnapshot> change)
    {
        lock (_gate)
        {
            entry.Snapshot = change(entry.Snapshot) with { UpdatedAt = _time.GetUtcNow() };
            return entry.Snapshot;
        }
    }

    /// <summary>Drops finished jobs past their retention, then the oldest finished jobs beyond the cap.</summary>
    private void Prune(DateTimeOffset now)
    {
        var finished = _jobs.Values
            .Where(entry => entry.Snapshot.State != VmJobState.Running)
            .OrderBy(entry => entry.Snapshot.UpdatedAt)
            .ToList();
        var excess = finished.Count - MaxFinishedJobs;
        foreach (var entry in finished)
        {
            if (excess > 0 || entry.Snapshot.UpdatedAt + Retention <= now)
            {
                _jobs.Remove(entry.Snapshot.Id);
                excess--;
            }
        }
    }

    private sealed class Entry(VmJobSnapshot snapshot)
    {
        public VmJobSnapshot Snapshot { get; set; } = snapshot;

        public List<IDisposable> Holds { get; } = [];

        public Task? Completion { get; set; }
    }
}
