using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>
/// Turns Performance mode on and off. Every change needs the VM off: a GPU partition, fixed memory, and
/// the MMIO gaps can only change then, and there is deliberately no shut-down-and-apply. The state is
/// checked before anything else, so a running VM is never touched.
/// </summary>
public sealed class VmPerformanceService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVPerformance _performance;
    private readonly IHyperVCompute _compute;
    private readonly IHostGpuReader _gpus;
    private readonly IHostCapacityReader _capacity;
    private readonly PerformanceStore _store;
    private readonly VmOperationLocks _locks;
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VmPerformanceService> _logger;

    public VmPerformanceService(
        IVmInventory inventory,
        IHyperVPerformance performance,
        IHyperVCompute compute,
        IHostGpuReader gpus,
        IHostCapacityReader capacity,
        PerformanceStore store,
        VmOperationLocks locks,
        VmJobStore jobs,
        LifecycleOptions options,
        TimeProvider time,
        ILogger<VmPerformanceService> logger)
    {
        _inventory = inventory;
        _performance = performance;
        _compute = compute;
        _gpus = gpus;
        _capacity = capacity;
        _store = store;
        _locks = locks;
        _jobs = jobs;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<VmPerformance> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        _ = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var record = _store.Find(vmId);
        var attached = await _performance.HasGpuPartitionAsync(vmId, cancellationToken).ConfigureAwait(false);
        var warnings = new List<ValidationIssue>();
        if (record is not null && !attached)
        {
            warnings.Add(new("gpu", "The GPU partition adapter was removed outside HyperHarbor. Apply Performance mode again."));
        }

        GuestDriverStatus? driver = null;
        if (record?.Guest is { } guest)
        {
            var hostVersion = await HostDriverVersionAsync(record.Settings, cancellationToken).ConfigureAwait(false);
            driver = new GuestDriverStatus(
                guest.Vendor,
                hostVersion,
                guest.DriverVersion,
                guest.CopiedAt,
                hostVersion is not null && !GpuIdentity.SameVersion(hostVersion, guest.DriverVersion),
                guest.RebootRequired);
        }

        return new VmPerformance(vmId, record is not null, record?.Settings, attached, driver, warnings);
    }

    /// <summary>Validates the settings and starts a job that applies them to the off VM.</summary>
    /// <exception cref="LifecycleConflictException">The VM is not off (code vmMustBeOff), or the host has no partitionable GPU (code gpuUnavailable).</exception>
    /// <exception cref="LifecycleValidationException">A value is invalid.</exception>
    /// <exception cref="ResourceWarningsException">Warnings were not acknowledged.</exception>
    public async Task<VmJobSnapshot> ApplyAsync(Guid vmId, Guid userId, PerformanceSettings request, Action<VmJobSnapshot>? onFinished, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        RequireOff(vm);

        var gpus = await _gpus.ReadAsync(cancellationToken).ConfigureAwait(false);
        var (settings, plan) = PerformanceValidator.Check(request, gpus, _capacity.Read(), _options.HostMemoryReserveMb);

        return _jobs.Start(VmJobKind.ApplyPerformance, vmId, userId, "Setting fixed memory and processors",
            context => RunAsync(vm.Name, vmId, settings, plan, context), onFinished);
    }

    /// <summary>Removes the GPU partition and restores Hyper-V's defaults. Memory stays fixed and checkpoints stay off.</summary>
    /// <exception cref="LifecycleConflictException">The VM is not off (code vmMustBeOff).</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task RemoveAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        RequireOff(vm);
        using (_locks.Acquire(vmId, "turning Performance mode off"))
        {
            await _performance.RemoveAsync(vmId, cancellationToken).ConfigureAwait(false);
        }

        _store.Remove(vmId);
        _logger.LogInformation("Turned Performance mode off for {Name} ({VmId}).", vm.Name, vmId);
    }

    private async Task RunAsync(string name, Guid vmId, PerformanceSettings settings, PerformancePlan plan, VmJobContext context)
    {
        // The VM could have been started between the request and the job.
        var vm = await _inventory.GetAsync(vmId, context.Stopping).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        RequireOff(vm);

        var current = await _compute.ReadAsync(vmId, context.Stopping).ConfigureAwait(false);
        var change = new ComputeChange(settings.ProcessorCount, settings.MemoryMb, settings.MemoryMb, DynamicMemory: false);
        var desired = current with
        {
            ProcessorCount = settings.ProcessorCount,
            StartupMemoryMb = settings.MemoryMb,
            MaximumMemoryMb = settings.MemoryMb,
            DynamicMemory = false,
        };
        await _compute.ApplyAsync(vmId, change, desired, context.Stopping).ConfigureAwait(false);

        context.Report("Adding the GPU partition and turning checkpoints off", 30);
        await _performance.ApplyAsync(vmId, plan, context.Stopping).ConfigureAwait(false);

        if (settings.MoveStorageTo is { } folder)
        {
            context.Report($"Moving the VM to {folder}", 50);
            await _performance.MoveStorageAsync(vmId, folder, percent => context.Report($"Moving the VM to {folder}", 50 + (percent * 49 / 100)), context.Stopping).ConfigureAwait(false);
        }

        _store.Save(new PerformanceRecord(vmId, settings with { MoveStorageTo = null }, _time.GetUtcNow(), _store.Find(vmId)?.Guest));
        _logger.LogInformation("Applied Performance mode to {Name} ({VmId}).", name, vmId);
    }

    /// <summary>The current driver version of the GPU the VM's partition uses.</summary>
    internal async Task<string?> HostDriverVersionAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        var gpus = await _gpus.ReadAsync(cancellationToken).ConfigureAwait(false);
        var gpu = gpus.FirstOrDefault(item => item.Partitionable && string.Equals(item.InstancePath, settings.Gpu?.InstancePath, StringComparison.OrdinalIgnoreCase))
            ?? gpus.FirstOrDefault(item => item.Partitionable);
        return gpu?.DriverVersion;
    }

    private static void RequireOff(Vm vm)
    {
        if (vm.State != VmState.Off)
        {
            throw new LifecycleConflictException(
                $"{vm.Name} must be off to change Performance mode; it is {vm.State.ToString().ToLowerInvariant()}. Shut it down first.",
                ContractInfo.ProblemCodes.VmMustBeOff);
        }
    }
}
