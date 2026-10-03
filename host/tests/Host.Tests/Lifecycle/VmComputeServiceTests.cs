using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmComputeServiceTests : IDisposable
{
    private static readonly Guid VmId = Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b");
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly FakeVmInventory _inventory = new();
    private readonly FakeHyperVCompute _compute = new();
    private readonly StatefulPowerInvoker _power;
    private readonly FakeHostCapacity _capacity = new();
    private readonly VmOperationLocks _locks = new();
    private readonly VmJobStore _jobs;
    private readonly LifecycleOptions _options = new();
    private readonly VmComputeService _service;

    public VmComputeServiceTests()
    {
        _power = new StatefulPowerInvoker(_inventory);
        _jobs = new VmJobStore(_locks, TimeProvider.System, NullLogger<VmJobStore>.Instance);
        _service = new VmComputeService(_inventory, _compute, _power, _capacity, _locks, _jobs, _options, TimeProvider.System, NullLogger<VmComputeService>.Instance);
        _inventory.Vms.Add(FakeVmInventory.CreateVm(VmId, "Dev", VmState.Off));
        _compute.States[VmId] = new ComputeState(2, 4096, 4096, false, false, false, 1);
    }

    public void Dispose() => _jobs.Dispose();

    [Fact]
    public async Task Get_ReportsSettings_AndWhichNeedTheVmOff()
    {
        var settings = await _service.GetAsync(VmId, CancellationToken.None);

        Assert.Equal(2, settings.ProcessorCount);
        Assert.Contains(ComputeSetting.MaximumMemoryMb, settings.RequiresOff);
        Assert.DoesNotContain(ComputeSetting.MacAddressSpoofing, settings.RequiresOff);
    }

    [Fact]
    public void MaximumMemory_CanChangeWhileRunning_OnlyWithDynamicMemory()
    {
        Assert.DoesNotContain(ComputeSetting.MaximumMemoryMb, VmComputeService.RequiresOff(true, true));
        Assert.Contains(ComputeSetting.MaximumMemoryMb, VmComputeService.RequiresOff(false, true));
        Assert.Contains(ComputeSetting.MaximumMemoryMb, VmComputeService.RequiresOff(true, false));
    }

    [Fact]
    public async Task OffVm_AppliesAtOnce()
    {
        var update = await UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 4, NestedVirtualization: true));

        Assert.Null(update.Job);
        Assert.Equal(4, update.Settings!.ProcessorCount);
        Assert.True(update.Settings.NestedVirtualization);
        var (_, change) = Assert.Single(_compute.Applied);
        Assert.Equal(4, change.ProcessorCount);
        Assert.Null(change.StartupMemoryMb);
    }

    [Fact]
    public async Task UnchangedValues_ApplyNothing()
    {
        var update = await UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 2, DynamicMemory: false));

        Assert.NotNull(update.Settings);
        Assert.Empty(_compute.Applied);
    }

    [Fact]
    public async Task RunningVm_ChangeThatNeedsOff_IsRefused_WithRequiresShutdown()
    {
        _inventory.SetState(VmId, VmState.Running);

        var ex = await Assert.ThrowsAsync<LifecycleConflictException>(() => UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 4)));

        Assert.Equal(ContractInfo.ProblemCodes.RequiresShutdown, ex.Code);
        Assert.Contains("processor count", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_compute.Applied);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task RunningVm_MacSpoofing_AppliesAtOnce()
    {
        _inventory.SetState(VmId, VmState.Running);

        var update = await UpdateAsync(new UpdateVmComputeRequest(MacAddressSpoofing: true));

        Assert.True(update.Settings!.MacAddressSpoofing);
        Assert.Empty(_power.Calls);
    }

    [Fact]
    public async Task RunningVm_WithDynamicMemory_CanRaiseItsMaximum()
    {
        _inventory.SetState(VmId, VmState.Running);
        _compute.States[VmId] = new ComputeState(2, 2048, 4096, true, false, false, 1);

        var update = await UpdateAsync(new UpdateVmComputeRequest(MaximumMemoryMb: 8192));

        Assert.Equal(8192, update.Settings!.MaximumMemoryMb);
    }

    [Fact]
    public async Task ShutDownToApply_ShutsDownAppliesAndRestarts()
    {
        _inventory.SetState(VmId, VmState.Running);

        var update = await UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 4, ShutDownToApply: true));

        Assert.Null(update.Settings);
        Assert.Equal(VmJobKind.ApplyCompute, update.Job!.Kind);
        await _jobs.WhenFinished(update.Job.Id);
        Assert.Equal(VmJobState.Succeeded, _jobs.Get(update.Job.Id, UserId)!.State);
        Assert.Equal([VmAction.Shutdown, VmAction.Start], _power.Calls);
        Assert.Equal(4, _compute.States[VmId].ProcessorCount);
    }

    [Fact]
    public async Task GuestThatDoesNotShutDown_FailsTheJob_WithoutForcingItOff()
    {
        _inventory.SetState(VmId, VmState.Running);
        _power.IgnoreShutdown = true;
        _options.ShutdownTimeoutSeconds = 0;

        var update = await UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 4, ShutDownToApply: true));
        await _jobs.WhenFinished(update.Job!.Id);

        var failed = _jobs.Get(update.Job.Id, UserId)!;
        Assert.Equal(VmJobState.Failed, failed.State);
        Assert.Contains("did not shut down", failed.ErrorDetail, StringComparison.Ordinal);
        Assert.Equal([VmAction.Shutdown], _power.Calls);
        Assert.Empty(_compute.Applied);
    }

    [Theory]
    [InlineData(VmState.Saved)]
    [InlineData(VmState.Paused)]
    public async Task SavedOrPausedVm_IsRefused(VmState state)
    {
        _inventory.SetState(VmId, state);

        await Assert.ThrowsAsync<LifecycleConflictException>(() => UpdateAsync(new UpdateVmComputeRequest(MacAddressSpoofing: true)));
        Assert.Empty(_compute.Applied);
    }

    [Fact]
    public async Task NestedVirtualizationWithDynamicMemory_IsInvalid()
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() =>
            UpdateAsync(new UpdateVmComputeRequest(NestedVirtualization: true, DynamicMemory: true, MaximumMemoryMb: 8192)));

        Assert.Contains(ex.Errors, error => error.Field == "dynamicMemory");
    }

    [Fact]
    public async Task DeveloperPreset_IsValid()
    {
        _compute.States[VmId] = new ComputeState(2, 2048, 8192, true, false, false, 1);

        var update = await UpdateAsync(new UpdateVmComputeRequest(NestedVirtualization: true, DynamicMemory: false, MacAddressSpoofing: true));

        var settings = update.Settings!;
        Assert.True(settings.NestedVirtualization);
        Assert.False(settings.DynamicMemory);
        Assert.True(settings.MacAddressSpoofing);
        Assert.Equal(2048, settings.MaximumMemoryMb);
    }

    [Fact]
    public async Task MoreProcessorsThanTheHost_IsInvalid()
    {
        var ex = await Assert.ThrowsAsync<LifecycleValidationException>(() => UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 64)));

        Assert.Contains(ex.Errors, error => error.Field == "processorCount");
    }

    [Fact]
    public async Task RaisingStartupMemoryPastTheReserve_IsAWarning_UntilAcknowledged()
    {
        _capacity.Capacity = new HostCapacity(16, 32768, 9000);

        var warned = await Assert.ThrowsAsync<ResourceWarningsException>(() => UpdateAsync(new UpdateVmComputeRequest(StartupMemoryMb: 8192)));
        Assert.Equal("startupMemoryMb", Assert.Single(warned.Warnings).Field);

        var update = await UpdateAsync(new UpdateVmComputeRequest(StartupMemoryMb: 8192, AcknowledgeWarnings: true));
        Assert.Equal(8192, update.Settings!.StartupMemoryMb);
    }

    [Fact]
    public async Task LoweringMemory_NeverWarns()
    {
        _capacity.Capacity = new HostCapacity(16, 32768, 100);

        var update = await UpdateAsync(new UpdateVmComputeRequest(StartupMemoryMb: 2048));

        Assert.Equal(2048, update.Settings!.StartupMemoryMb);
    }

    [Fact]
    public async Task MacSpoofingWithoutAnAdapter_IsInvalid()
    {
        _compute.States[VmId] = _compute.States[VmId] with { NetworkAdapterCount = 0 };

        await Assert.ThrowsAsync<LifecycleValidationException>(() => UpdateAsync(new UpdateVmComputeRequest(MacAddressSpoofing: true)));
    }

    [Fact]
    public async Task BusyVm_IsRefused()
    {
        using var held = _locks.Acquire(VmId, "deleting the virtual machine");

        await Assert.ThrowsAsync<VmBusyException>(() => UpdateAsync(new UpdateVmComputeRequest(ProcessorCount: 4)));
    }

    private Task<VmComputeUpdate> UpdateAsync(UpdateVmComputeRequest request) =>
        _service.UpdateAsync(VmId, UserId, request, ToContract, null, CancellationToken.None);

    private static VmJob ToContract(VmJobSnapshot job) =>
        new(job.Id, job.Kind, job.VmId, job.State, job.Step, job.PercentComplete, job.CreatedAt, job.UpdatedAt, null);

    /// <summary>Shutdown turns the fake VM off and start turns it on, unless told to ignore shutdown.</summary>
    private sealed class StatefulPowerInvoker(FakeVmInventory inventory) : IHyperVPowerInvoker
    {
        public List<VmAction> Calls { get; } = [];

        public bool IgnoreShutdown { get; set; }

        public Task InvokeAsync(Guid vmId, VmAction action, CancellationToken cancellationToken)
        {
            lock (Calls)
            {
                Calls.Add(action);
            }

            if (action == VmAction.Shutdown && !IgnoreShutdown)
            {
                inventory.SetState(vmId, VmState.Off);
            }
            else if (action == VmAction.Start)
            {
                inventory.SetState(vmId, VmState.Running);
            }

            return Task.CompletedTask;
        }
    }
}
