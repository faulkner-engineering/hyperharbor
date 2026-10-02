using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Power;

public class VmActionPolicyTests
{
    [Theory]
    [InlineData(VmAction.Start, VmState.Off)]
    [InlineData(VmAction.Start, VmState.Saved)]
    [InlineData(VmAction.Start, VmState.Paused)]
    [InlineData(VmAction.Shutdown, VmState.Running)]
    [InlineData(VmAction.Restart, VmState.Running)]
    [InlineData(VmAction.Save, VmState.Running)]
    [InlineData(VmAction.Save, VmState.Paused)]
    [InlineData(VmAction.TurnOff, VmState.Running)]
    [InlineData(VmAction.TurnOff, VmState.Paused)]
    [InlineData(VmAction.TurnOff, VmState.Starting)]
    [InlineData(VmAction.TurnOff, VmState.Stopping)]
    public void IsAllowed_ReturnsTrueForValidTransitions(VmAction action, VmState state)
    {
        Assert.True(VmActionPolicy.IsAllowed(state, action));
    }

    [Theory]
    [InlineData(VmAction.Start, VmState.Running)]
    [InlineData(VmAction.Start, VmState.Starting)]
    [InlineData(VmAction.Shutdown, VmState.Off)]
    [InlineData(VmAction.Shutdown, VmState.Paused)]
    [InlineData(VmAction.Restart, VmState.Saved)]
    [InlineData(VmAction.Save, VmState.Off)]
    [InlineData(VmAction.Save, VmState.Saved)]
    [InlineData(VmAction.TurnOff, VmState.Off)]
    [InlineData(VmAction.TurnOff, VmState.Saved)]
    [InlineData(VmAction.TurnOff, VmState.Saving)]
    public void IsAllowed_ReturnsFalseForInvalidTransitions(VmAction action, VmState state)
    {
        Assert.False(VmActionPolicy.IsAllowed(state, action));
    }

    [Fact]
    public void IsAllowed_RejectsEveryActionInOtherState()
    {
        Assert.All(Enum.GetValues<VmAction>(), action => Assert.False(VmActionPolicy.IsAllowed(VmState.Other, action)));
    }
}
