using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Power;

/// <summary>Thrown when no virtual machine has the requested ID.</summary>
public sealed class VmNotFoundException : Exception
{
    public VmNotFoundException(Guid vmId)
        : base($"Virtual machine {vmId} was not found.")
    {
        VmId = vmId;
    }

    public Guid VmId { get; }
}

/// <summary>Thrown when an action is not valid for the virtual machine's current state.</summary>
public sealed class VmActionNotAllowedException : Exception
{
    public VmActionNotAllowedException(VmAction action, VmState? state, string message)
        : base(message)
    {
        Action = action;
        State = state;
    }

    public VmAction Action { get; }

    /// <summary>The state at the time of the request, when known.</summary>
    public VmState? State { get; }
}

/// <summary>Thrown when Hyper-V rejects a request with a return code that has no more specific mapping.</summary>
public sealed class HyperVOperationException : Exception
{
    public HyperVOperationException(string operation, uint returnCode)
        : base($"Hyper-V {operation} failed with return code {returnCode}.")
    {
        Operation = operation;
        ReturnCode = returnCode;
    }

    public string Operation { get; }

    public uint ReturnCode { get; }
}
