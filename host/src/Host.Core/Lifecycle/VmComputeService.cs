using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// Reads and changes processor, memory, nested virtualization, and MAC address spoofing settings.
/// Settings in <see cref="RequiresOff"/> can change only while the VM is off. For a running VM,
/// the caller can ask for a job that shuts the guest down, applies the changes, and starts it again.
/// </summary>
public sealed class VmComputeService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IVmInventory _inventory;
    private readonly IHyperVCompute _compute;
    private readonly IHyperVPowerInvoker _power;
    private readonly IHostCapacityReader _capacity;
    private readonly VmOperationLocks _locks;
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<VmComputeService> _logger;

    public VmComputeService(
        IVmInventory inventory,
        IHyperVCompute compute,
        IHyperVPowerInvoker power,
        IHostCapacityReader capacity,
        VmOperationLocks locks,
        VmJobStore jobs,
        LifecycleOptions options,
        TimeProvider time,
        ILogger<VmComputeService> logger)
    {
        _inventory = inventory;
        _compute = compute;
        _power = power;
        _capacity = capacity;
        _locks = locks;
        _jobs = jobs;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Settings that need the VM off. Maximum memory can change on a running VM only while dynamic
    /// memory is on before and after the change; MAC address spoofing can always change.
    /// </summary>
    public static IReadOnlyList<ComputeSetting> RequiresOff(bool dynamicMemoryBefore, bool dynamicMemoryAfter)
    {
        var settings = new List<ComputeSetting>
        {
            ComputeSetting.ProcessorCount,
            ComputeSetting.StartupMemoryMb,
            ComputeSetting.DynamicMemory,
            ComputeSetting.NestedVirtualization,
        };
        if (!(dynamicMemoryBefore && dynamicMemoryAfter))
        {
            settings.Insert(2, ComputeSetting.MaximumMemoryMb);
        }

        return settings;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<VmComputeSettings> GetAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var current = await _compute.ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        return ToContract(vmId, vm.State, current);
    }

    /// <summary>Applies the changes now when the VM's state allows it; otherwise starts a job if the caller asked for one.</summary>
    /// <exception cref="LifecycleValidationException">The resulting settings are invalid.</exception>
    /// <exception cref="ResourceWarningsException">Warnings were not acknowledged.</exception>
    /// <exception cref="LifecycleConflictException">A change needs the VM off (code requiresShutdown), or the VM is in a state that allows no changes.</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task<VmComputeUpdate> UpdateAsync(
        Guid vmId,
        Guid userId,
        UpdateVmComputeRequest request,
        Func<VmJobSnapshot, VmJob> toContract,
        Action<VmJobSnapshot>? onFinished,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var current = await _compute.ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        var (change, desired) = Merge(current, request);
        Validate(vm.State, current, desired, change, request.AcknowledgeWarnings);

        var changed = Changed(change);
        if (changed.Count == 0)
        {
            return new VmComputeUpdate(ToContract(vmId, vm.State, current), null);
        }

        var needOff = changed.Intersect(RequiresOff(current.DynamicMemory, desired.DynamicMemory)).ToList();
        switch (vm.State)
        {
            case VmState.Off:
            case VmState.Running when needOff.Count == 0:
                using (_locks.Acquire(vmId, VmJobStore.Describe(VmJobKind.ApplyCompute)))
                {
                    await _compute.ApplyAsync(vmId, change, desired, cancellationToken).ConfigureAwait(false);
                }

                _logger.LogInformation("Changed {Settings} of {Name} ({VmId}).", string.Join(", ", changed), vm.Name, vmId);
                return new VmComputeUpdate(await GetAsync(vmId, CancellationToken.None).ConfigureAwait(false), null);

            case VmState.Running when request.ShutDownToApply:
                var job = _jobs.Start(VmJobKind.ApplyCompute, vmId, userId, "Shutting down the guest",
                    context => ShutDownApplyStartAsync(vmId, vm.Name, change, desired, context), onFinished);
                return new VmComputeUpdate(null, toContract(job));

            case VmState.Running:
                throw new LifecycleConflictException(
                    $"{vm.Name} must be off to change {Describe(needOff)}. Shut it down first, or choose to shut down, apply, and restart.",
                    ContractInfo.ProblemCodes.RequiresShutdown);

            default:
                throw new LifecycleConflictException(
                    $"Settings can be changed while {vm.Name} is off or running; it is {vm.State.ToString().ToLowerInvariant()}.");
        }
    }

    private async Task ShutDownApplyStartAsync(Guid vmId, string name, ComputeChange change, ComputeState desired, VmJobContext context)
    {
        await _power.InvokeAsync(vmId, VmAction.Shutdown, context.Stopping).ConfigureAwait(false);

        var deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(_options.ShutdownTimeoutSeconds);
        while (true)
        {
            var vm = await _inventory.GetAsync(vmId, context.Stopping).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
            if (vm.State == VmState.Off)
            {
                break;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                throw new LifecycleConflictException(
                    $"{name} did not shut down within {_options.ShutdownTimeoutSeconds} seconds, so the settings were not changed. It was not forced off.");
            }

            var elapsed = _options.ShutdownTimeoutSeconds - (deadline - _time.GetUtcNow()).TotalSeconds;
            context.Report("Shutting down the guest", (int)(elapsed * 50 / Math.Max(1, _options.ShutdownTimeoutSeconds)));
            await Task.Delay(PollInterval, _time, context.Stopping).ConfigureAwait(false);
        }

        context.Report("Applying settings", 60);
        await _compute.ApplyAsync(vmId, change, desired, context.Stopping).ConfigureAwait(false);
        _logger.LogInformation("Changed settings of {Name} ({VmId}) while it was shut down.", name, vmId);

        context.Report("Starting the virtual machine", 85);
        await _power.InvokeAsync(vmId, VmAction.Start, context.Stopping).ConfigureAwait(false);
    }

    private void Validate(VmState state, ComputeState current, ComputeState desired, ComputeChange change, bool acknowledgeWarnings)
    {
        var errors = new List<ValidationIssue>();
        var warnings = new List<ValidationIssue>();
        var host = _capacity.Read();
        VmSettingsValidator.Check(
            new ComputeRequest(desired.ProcessorCount, desired.StartupMemoryMb, desired.MaximumMemoryMb, desired.DynamicMemory, desired.NestedVirtualization),
            host,
            _options.HostMemoryReserveMb,
            errors,
            warnings);

        // The reserve warning concerns memory the VM would take; skip it unless startup memory grows.
        // A running VM already holds its current startup memory, so only the increase counts.
        warnings.RemoveAll(warning => warning.Field == "startupMemoryMb");
        if (change.StartupMemoryMb is not null && desired.StartupMemoryMb > current.StartupMemoryMb)
        {
            var taken = state == VmState.Running ? desired.StartupMemoryMb - current.StartupMemoryMb : desired.StartupMemoryMb;
            var left = host.AvailableMemoryMb - taken;
            if (left < _options.HostMemoryReserveMb)
            {
                warnings.Add(new("startupMemoryMb",
                    $"This would leave the host about {VmSettingsValidator.Mb(Math.Max(0, left))} free, below the reserve of {VmSettingsValidator.Mb(_options.HostMemoryReserveMb)}."));
            }
        }

        if (change.MacAddressSpoofing is true && current.NetworkAdapterCount == 0)
        {
            errors.Add(new("macAddressSpoofing", "The VM has no network adapter connected to a switch."));
        }

        if (errors.Count > 0)
        {
            throw new LifecycleValidationException(errors[0].Message, errors);
        }

        if (warnings.Count > 0 && !acknowledgeWarnings)
        {
            throw new ResourceWarningsException(warnings);
        }
    }

    /// <summary>Keeps only real changes, and works out the full settings after them.</summary>
    private static (ComputeChange Change, ComputeState Desired) Merge(ComputeState current, UpdateVmComputeRequest request)
    {
        static T? Diff<T>(T? requested, T current)
            where T : struct => requested is { } value && !EqualityComparer<T>.Default.Equals(value, current) ? value : null;

        var dynamicMemory = request.DynamicMemory ?? current.DynamicMemory;
        var startup = request.StartupMemoryMb ?? current.StartupMemoryMb;

        // Static memory has no separate maximum; keep it equal to the startup memory.
        var maximum = dynamicMemory ? request.MaximumMemoryMb ?? Math.Max(current.MaximumMemoryMb, startup) : startup;

        var change = new ComputeChange(
            Diff(request.ProcessorCount, current.ProcessorCount),
            Diff(request.StartupMemoryMb, current.StartupMemoryMb),
            dynamicMemory && Diff<long>(maximum, current.MaximumMemoryMb) is { } newMaximum ? newMaximum : null,
            Diff(request.DynamicMemory, current.DynamicMemory),
            Diff(request.NestedVirtualization, current.NestedVirtualization),
            Diff(request.MacAddressSpoofing, current.MacAddressSpoofing));
        var desired = current with
        {
            ProcessorCount = change.ProcessorCount ?? current.ProcessorCount,
            StartupMemoryMb = startup,
            MaximumMemoryMb = maximum,
            DynamicMemory = dynamicMemory,
            NestedVirtualization = change.NestedVirtualization ?? current.NestedVirtualization,
            MacAddressSpoofing = change.MacAddressSpoofing ?? current.MacAddressSpoofing,
        };
        return (change, desired);
    }

    private static List<ComputeSetting> Changed(ComputeChange change)
    {
        var changed = new List<ComputeSetting>();
        if (change.ProcessorCount is not null)
        {
            changed.Add(ComputeSetting.ProcessorCount);
        }

        if (change.StartupMemoryMb is not null)
        {
            changed.Add(ComputeSetting.StartupMemoryMb);
        }

        if (change.MaximumMemoryMb is not null)
        {
            changed.Add(ComputeSetting.MaximumMemoryMb);
        }

        if (change.DynamicMemory is not null)
        {
            changed.Add(ComputeSetting.DynamicMemory);
        }

        if (change.NestedVirtualization is not null)
        {
            changed.Add(ComputeSetting.NestedVirtualization);
        }

        if (change.MacAddressSpoofing is not null)
        {
            changed.Add(ComputeSetting.MacAddressSpoofing);
        }

        return changed;
    }

    private static string Describe(IEnumerable<ComputeSetting> settings) => string.Join(", ", settings.Select(setting => setting switch
    {
        ComputeSetting.ProcessorCount => "the processor count",
        ComputeSetting.StartupMemoryMb => "the startup memory",
        ComputeSetting.MaximumMemoryMb => "the maximum memory",
        ComputeSetting.DynamicMemory => "dynamic memory",
        ComputeSetting.NestedVirtualization => "nested virtualization",
        _ => "MAC address spoofing",
    }));

    private static VmComputeSettings ToContract(Guid vmId, VmState state, ComputeState current) => new(
        vmId,
        state,
        current.ProcessorCount,
        current.StartupMemoryMb,
        current.MaximumMemoryMb,
        current.DynamicMemory,
        current.NestedVirtualization,
        current.MacAddressSpoofing,
        current.NetworkAdapterCount,
        RequiresOff(current.DynamicMemory, current.DynamicMemory));
}
