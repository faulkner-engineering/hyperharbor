using HyperHarbor.Host.Core.Audit;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>The outcome of shutting down the running Performance mode VMs.</summary>
/// <param name="Stopped">VMs that reached Off.</param>
/// <param name="StillRunning">VMs that were not off when the wait ended.</param>
public sealed record GpuVmShutdownResult(IReadOnlyList<string> Stopped, IReadOnlyList<string> StillRunning);

/// <summary>
/// Shuts down the running Performance mode VMs before the host restarts or shuts down. A GPU partition
/// VM cannot be saved, so without this Hyper-V turns it off (its stop action), losing unsaved work in
/// the guest. The coordinator asks each guest to shut down and waits; it never turns a VM off itself.
/// </summary>
public sealed class GpuVmShutdownCoordinator
{
    /// <summary>How often the coordinator checks whether the VMs are off.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IVmInventory _inventory;
    private readonly IVmPowerService _power;
    private readonly PerformanceStore _store;
    private readonly IAuditLog? _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<GpuVmShutdownCoordinator> _logger;

    public GpuVmShutdownCoordinator(
        IVmInventory inventory,
        IVmPowerService power,
        PerformanceStore store,
        IAuditLog? audit,
        TimeProvider time,
        ILogger<GpuVmShutdownCoordinator> logger)
    {
        _inventory = inventory;
        _power = power;
        _store = store;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    /// <summary>Running VMs that are in Performance mode.</summary>
    public async Task<IReadOnlyList<Vm>> RunningAsync(CancellationToken cancellationToken)
    {
        var vms = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        return vms.Where(vm => vm.State == VmState.Running && _store.Find(vm.Id) is not null).ToList();
    }

    /// <summary>
    /// Asks every running Performance mode VM to shut down and waits until they are off or
    /// <paramref name="timeout"/> passes. A VM that refuses (for example with no guest OS running) is
    /// reported as still running.
    /// </summary>
    public async Task<GpuVmShutdownResult> StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var running = await RunningAsync(cancellationToken).ConfigureAwait(false);
        if (running.Count == 0)
        {
            return new GpuVmShutdownResult([], []);
        }

        _logger.LogInformation("Shutting down {Count} Performance mode VMs before the host shuts down: {Vms}.", running.Count, string.Join(", ", running.Select(vm => vm.Name)));
        foreach (var vm in running)
        {
            try
            {
                await _power.PerformAsync(vm.Id, VmAction.Shutdown, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Could not ask {Vm} to shut down: {Message}", vm.Name, ex.Message);
            }
        }

        var deadline = _time.GetUtcNow() + timeout;
        var pending = running.Select(vm => vm.Id).ToHashSet();
        while (true)
        {
            var states = (await _inventory.ListAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(vm => vm.Id, vm => vm.State);
            pending.RemoveWhere(id => !states.TryGetValue(id, out var state) || state == VmState.Off);
            if (pending.Count == 0 || _time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
        }

        var result = new GpuVmShutdownResult(
            running.Where(vm => !pending.Contains(vm.Id)).Select(vm => vm.Name).ToList(),
            running.Where(vm => pending.Contains(vm.Id)).Select(vm => vm.Name).ToList());
        if (result.StillRunning.Count > 0)
        {
            _logger.LogWarning("These Performance mode VMs were still running when the wait ended: {Vms}.", string.Join(", ", result.StillRunning));
        }

        Audit(result);
        return result;
    }

    private void Audit(GpuVmShutdownResult result)
    {
        try
        {
            _audit?.Write(new AuditEntry(
                _time.GetUtcNow(),
                "shutDownGpuVms",
                result.StillRunning.Count == 0 ? AuditOutcome.Succeeded : AuditOutcome.Failed,
                Detail: $"Host shutdown. stopped={string.Join(", ", result.Stopped)}; stillRunning={string.Join(", ", result.StillRunning)}"));
        }
        catch (AuditUnavailableException ex)
        {
            _logger.LogWarning(ex, "Could not audit the shutdown of the Performance mode VMs.");
        }
    }
}
