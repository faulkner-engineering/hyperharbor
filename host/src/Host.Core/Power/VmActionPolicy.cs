using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Power;

/// <summary>
/// Defines which power actions are valid from each virtual machine state.
/// </summary>
public static class VmActionPolicy
{
    public static bool IsAllowed(VmState state, VmAction action) => action switch
    {
        VmAction.Start => state is VmState.Off or VmState.Saved or VmState.Paused,
        VmAction.Shutdown => state is VmState.Running,
        VmAction.Restart => state is VmState.Running,
        VmAction.Save => state is VmState.Running or VmState.Paused,
        VmAction.TurnOff => state is VmState.Running or VmState.Paused or VmState.Starting or VmState.Stopping,
        _ => false,
    };
}
