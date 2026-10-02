namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Virtual machine state. Schema: VmState.</summary>
public enum VmState
{
    Running,
    Off,
    Saved,
    Paused,
    Starting,
    Stopping,
    Saving,
    Pausing,
    Resuming,
    Other,
}
