namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Power action on a virtual machine. Schema: VmAction.</summary>
public enum VmAction
{
    /// <summary>Power on or resume from saved state.</summary>
    Start,

    /// <summary>Graceful guest shutdown through integration services.</summary>
    Shutdown,

    /// <summary>Immediate power off. Can lose unsaved guest data.</summary>
    TurnOff,

    /// <summary>Save memory state to disk and stop.</summary>
    Save,

    /// <summary>Graceful guest restart.</summary>
    Restart,
}
