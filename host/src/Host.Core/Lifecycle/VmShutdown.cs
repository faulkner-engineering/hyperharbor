using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Shuts a guest down for a job that needs the VM off, and waits for it; it never forces the VM off.</summary>
internal static class VmShutdown
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <param name="failure">The message when the guest does not shut down in time, saying what was left unchanged.</param>
    /// <param name="percentAtOff">Progress reported while waiting runs from 0 up to this.</param>
    /// <exception cref="LifecycleConflictException">The guest did not shut down within the timeout.</exception>
    public static async Task ShutDownAsync(
        IHyperVPowerInvoker power,
        IVmInventory inventory,
        TimeProvider time,
        int timeoutSeconds,
        Guid vmId,
        string failure,
        VmJobContext context,
        int percentAtOff = 50)
    {
        await power.InvokeAsync(vmId, VmAction.Shutdown, context.Stopping).ConfigureAwait(false);

        var deadline = time.GetUtcNow() + TimeSpan.FromSeconds(timeoutSeconds);
        while (true)
        {
            var vm = await inventory.GetAsync(vmId, context.Stopping).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
            if (vm.State == VmState.Off)
            {
                return;
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new LifecycleConflictException(failure);
            }

            var elapsed = timeoutSeconds - (deadline - time.GetUtcNow()).TotalSeconds;
            context.Report("Shutting down the guest", (int)(elapsed * percentAtOff / Math.Max(1, timeoutSeconds)));
            await Task.Delay(PollInterval, time, context.Stopping).ConfigureAwait(false);
        }
    }
}
