using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Power;

public sealed class GuestRequestDeadlineTests
{
    [Fact]
    public async Task ARequestTheGuestNeverAnswers_FailsAtTheDeadline_InsteadOfHanging()
    {
        var never = new TaskCompletionSource();

        var error = await Assert.ThrowsAsync<VmActionNotAllowedException>(
            () => CimHyperVPowerInvoker.WithDeadlineAsync(never.Task, VmAction.Shutdown, TimeSpan.FromMilliseconds(50)));

        Assert.Equal(VmAction.Shutdown, error.Action);
        Assert.Contains("did not answer the request to shut down", error.Message);
        Assert.Contains("turnOff", error.Message);

        // The abandoned call failing later is observed, not left to crash the process.
        never.SetException(new InvalidOperationException("late"));
    }

    [Fact]
    public async Task ARequestThatIsAnswered_PassesItsOutcomeThrough()
    {
        await CimHyperVPowerInvoker.WithDeadlineAsync(Task.CompletedTask, VmAction.Restart, TimeSpan.FromSeconds(5));

        var refused = new VmActionNotAllowedException(VmAction.Restart, null, "The guest is not responding.");
        var error = await Assert.ThrowsAsync<VmActionNotAllowedException>(
            () => CimHyperVPowerInvoker.WithDeadlineAsync(Task.FromException(refused), VmAction.Restart, TimeSpan.FromSeconds(5)));
        Assert.Same(refused, error);
    }

    [Fact]
    public void TheDeadline_LeavesTimeForASlowButHealthyGuest() =>
        Assert.InRange(CimHyperVPowerInvoker.GuestRequestTimeout, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5));
}
