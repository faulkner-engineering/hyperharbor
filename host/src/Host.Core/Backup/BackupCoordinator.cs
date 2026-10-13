using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup;

/// <summary>Runs one backup; the seam between the coordinator and the VM and host-state services.</summary>
public interface IBackupRunner
{
    Task<BackupRunRecord> RunVmAsync(VmBackupRequest request, Action<string, int> progress, CancellationToken cancellationToken);

    Task<BackupRunRecord> RunHostStateAsync(BackupTarget target, BackupTrigger trigger, Action<string, int> progress, CancellationToken cancellationToken);
}

public sealed class BackupRunner(VmBackupService vms, HostStateBackupService host) : IBackupRunner
{
    public Task<BackupRunRecord> RunVmAsync(VmBackupRequest request, Action<string, int> progress, CancellationToken cancellationToken) =>
        vms.RunAsync(request, progress, cancellationToken);

    public Task<BackupRunRecord> RunHostStateAsync(BackupTarget target, BackupTrigger trigger, Action<string, int> progress, CancellationToken cancellationToken) =>
        host.RunAsync(target, trigger, progress, cancellationToken);
}

/// <summary>
/// Decides when backups start. Each tick it notices shutdowns (debounced), schedule slots that came due, and manual
/// requests, then starts every waiting run whose conditions hold as a job. A run whose conditions keep failing is recorded as
/// skipped after <see cref="DeferLimit"/>. Each run holds a <c>HostActivity</c> request, so the host stays awake and a host
/// update waits for it.
/// </summary>
public sealed class BackupCoordinator(
    BackupConfigStore config,
    IVmInventory inventory,
    IVmSessionProbe sessions,
    Func<bool> gameActive,
    BackupHistoryStore history,
    IBackupRunner runner,
    VmJobStore jobs,
    Func<IDisposable?> beginActivity,
    TimeProvider time,
    ILogger<BackupCoordinator> logger)
{
    /// <summary>Jobs belong to a User; backups belong to the host, so they carry this one.</summary>
    public static readonly Guid HostUserId = Guid.Empty;

    public static readonly TimeSpan DeferLimit = TimeSpan.FromHours(12);

    public const string HostItem = "host";

    private readonly object _gate = new();
    private readonly ShutdownDebouncer _shutdowns = new();
    private readonly Dictionary<(string Item, string TargetId), Waiting> _waiting = [];
    private readonly HashSet<(string Item, string TargetId)> _running = [];

    public static string VmItem(Guid vmId) => $"vm:{vmId:D}";

    /// <summary>Whether any backup runs or waits for its conditions.</summary>
    public bool Busy
    {
        get
        {
            lock (_gate)
            {
                return _running.Count > 0;
            }
        }
    }

    public int WaitingCount
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count;
            }
        }
    }

    public async Task TickAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var local = time.GetLocalNow();
        var settings = config.Load();
        var targets = settings.Targets.Where(t => t.Enabled).ToDictionary(t => t.Id, StringComparer.Ordinal);
        var vms = await inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        var known = vms.ToDictionary(v => v.Id);

        // Shutdown trigger: the smallest debounce among a VM's policies that ask for it.
        foreach (var vm in vms)
        {
            var wanted = settings.Policies
                .Where(p => p.Enabled && p.Triggers.OnShutdown && VmIdOf(p) == vm.Id && targets.ContainsKey(p.TargetId))
                .Select(p => TimeSpan.FromMinutes(Math.Max(0, p.Triggers.DebounceMinutes)))
                .ToList();
            _shutdowns.Observe(vm.Id, vm.State, now, wanted.Count == 0 ? null : wanted.Min());
        }

        foreach (var vmId in _shutdowns.TakeDue(now))
        {
            foreach (var policy in settings.Policies.Where(p => p.Enabled && p.Triggers.OnShutdown && VmIdOf(p) == vmId && targets.ContainsKey(p.TargetId)))
            {
                Enqueue(VmItem(vmId), policy.TargetId, BackupTrigger.Shutdown, now);
            }
        }

        // Schedule trigger.
        foreach (var target in targets.Values.Where(t => t.VmRepository?.KeyState == RepositoryKeyState.Confirmed))
        {
            foreach (var policy in settings.Policies.Where(p => p.Enabled && p.Triggers.Schedule && p.TargetId == target.Id))
            {
                if (VmIdOf(policy) is { } vmId && known.ContainsKey(vmId))
                {
                    EnqueueIfDue(target, VmItem(vmId), local);
                }
            }
        }

        foreach (var target in targets.Values.Where(t => t.HostRepository?.KeyState == RepositoryKeyState.Confirmed))
        {
            EnqueueIfDue(target, HostItem, local);
        }

        await StartWaitingAsync(settings, known, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts a manual run now. Conditions are checked only when <paramref name="respectConditions"/> is set.</summary>
    /// <exception cref="BackupException">The target is unknown, disabled, or not ready, or the run is already going.</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public VmJobSnapshot StartManual(string targetId, Guid? vmId, Guid? userId = null, Action<VmJobSnapshot>? onFinished = null)
    {
        var settings = config.Load();
        var target = settings.Targets.FirstOrDefault(t => t.Id == targetId && t.Enabled)
            ?? throw new BackupException("The backup target does not exist or is disabled.");
        if (target.VmRepository?.KeyState != RepositoryKeyState.Confirmed || target.HostRepository?.KeyState != RepositoryKeyState.Confirmed)
        {
            throw new BackupException($"The backup target \"{target.Name}\" is not ready: its recovery key has not been confirmed.");
        }

        var item = vmId is { } id ? VmItem(id) : HostItem;
        var policy = vmId is { } vm
            ? settings.Policies.FirstOrDefault(p => p.TargetId == targetId && VmIdOf(p) == vm)
                ?? new BackupPolicy { VmId = vm.ToString("D"), TargetId = targetId, Retention = target.DefaultRetention, BandwidthKbps = target.DefaultBandwidthKbps }
            : null;
        var hold = beginActivity() ?? throw new BackupException("The host is about to update. Try again in a few minutes.");
        try
        {
            lock (_gate)
            {
                if (_running.Contains((item, targetId)))
                {
                    throw new BackupException("A backup of this item to this target is already running.");
                }
            }

            return StartJob(item, target, policy, BackupTrigger.Manual, hold, userId ?? HostUserId, onFinished);
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    private void EnqueueIfDue(BackupTarget target, string item, DateTimeOffset local)
    {
        var last = history.Last(item, target.Id)?.FinishedAt;
        if (ScheduleCalculator.IsDue(target.Schedule, local, last))
        {
            Enqueue(item, target.Id, BackupTrigger.Schedule, time.GetUtcNow());
        }
    }

    private void Enqueue(string item, string targetId, BackupTrigger trigger, DateTimeOffset now)
    {
        lock (_gate)
        {
            var key = (item, targetId);
            if (!_running.Contains(key) && !_waiting.ContainsKey(key))
            {
                _waiting[key] = new Waiting(trigger, now);
            }
        }
    }

    private async Task StartWaitingAsync(BackupConfig settings, IReadOnlyDictionary<Guid, Vm> known, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<((string Item, string TargetId) Key, Waiting Entry)> waiting;
        lock (_gate)
        {
            waiting = _waiting.Select(pair => (pair.Key, pair.Value)).ToList();
        }

        foreach (var (key, entry) in waiting)
        {
            var target = settings.Targets.FirstOrDefault(t => t.Id == key.TargetId && t.Enabled);
            var vmId = ParseVmItem(key.Item);
            var policy = vmId is { } id ? settings.Policies.FirstOrDefault(p => p.Enabled && p.TargetId == key.TargetId && VmIdOf(p) == id) : null;
            Vm? vm = null;
            if (target is null || (vmId is { } wanted && (!known.TryGetValue(wanted, out vm) || policy is null)))
            {
                Remove(key);
                continue;
            }

            var conditions = policy?.Conditions ?? target.DefaultConditions;
            var inputs = new ConditionInputs(
                vm is null ? null : await sessions.HasActiveSessionAsync(vm, cancellationToken).ConfigureAwait(false),
                gameActive(),
                TimeOnly.FromDateTime(time.GetLocalNow().DateTime));
            var decision = RunConditionEvaluator.Evaluate(conditions, inputs);
            if (!decision.Allowed)
            {
                if (now - entry.Since >= DeferLimit)
                {
                    RecordSkipped(key, target, vm, entry.Trigger, decision.Reason!, now);
                    Remove(key);
                }

                continue;
            }

            var hold = beginActivity();
            if (hold is null)
            {
                continue;
            }

            try
            {
                StartJob(key.Item, target, policy, entry.Trigger, hold);
                Remove(key);
            }
            catch (VmBusyException)
            {
                hold.Dispose();
                logger.LogInformation("Backup of {Item} waits: the virtual machine is busy.", key.Item);
            }
            catch (Exception ex)
            {
                hold.Dispose();
                logger.LogError(ex, "Backup of {Item} could not be started.", key.Item);
                Remove(key);
            }
        }
    }

    private VmJobSnapshot StartJob(string item, BackupTarget target, BackupPolicy? policy, BackupTrigger trigger, IDisposable hold, Guid? userId = null, Action<VmJobSnapshot>? onFinished = null)
    {
        var key = (item, target.Id);
        var vmId = ParseVmItem(item);
        lock (_gate)
        {
            _running.Add(key);
        }

        try
        {
            return jobs.Start(
                vmId is null ? VmJobKind.BackupHostState : VmJobKind.BackupVm,
                vmId,
                userId ?? HostUserId,
                "Starting the backup",
                async context =>
                {
                    try
                    {
                        var record = vmId is { } id
                            ? await runner.RunVmAsync(new VmBackupRequest(id, target, policy!, trigger), context.Report, context.Stopping).ConfigureAwait(false)
                            : await runner.RunHostStateAsync(target, trigger, context.Report, context.Stopping).ConfigureAwait(false);
                        if (record.Outcome == BackupOutcome.Failed)
                        {
                            throw new BackupException(record.Reason ?? "The backup failed.");
                        }
                    }
                    finally
                    {
                        hold.Dispose();
                        lock (_gate)
                        {
                            _running.Remove(key);
                        }
                    }
                },
                onFinished);
        }
        catch
        {
            lock (_gate)
            {
                _running.Remove(key);
            }

            throw;
        }
    }

    private void RecordSkipped((string Item, string TargetId) key, BackupTarget target, Vm? vm, BackupTrigger trigger, string reason, DateTimeOffset now)
    {
        logger.LogWarning("Backup of {Item} to {Target} skipped: {Reason}", key.Item, target.Name, reason);
        history.Add(new BackupRunRecord(
            Guid.NewGuid(), key.Item, vm?.Id, vm?.Name, target.Id, trigger, now, now, BackupOutcome.Skipped,
            "conditionsNotMet", null, 0, [reason]));
    }

    private void Remove((string Item, string TargetId) key)
    {
        lock (_gate)
        {
            _waiting.Remove(key);
        }
    }

    private static Guid? VmIdOf(BackupPolicy policy) => Guid.TryParse(policy.VmId, out var id) ? id : null;

    private static Guid? ParseVmItem(string item) =>
        item.StartsWith("vm:", StringComparison.Ordinal) && Guid.TryParse(item.AsSpan(3), out var id) ? id : null;

    private sealed record Waiting(BackupTrigger Trigger, DateTimeOffset Since);
}
