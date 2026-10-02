using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Wake;

/// <summary>
/// Asks the user at the host to approve Wake-on-LAN fixes. Implemented by the tray pipe server.
/// </summary>
public interface IWakeFixApprover
{
    bool CanRequestApproval { get; }

    void RequestApproval(WakeFixRequestedMessage request);
}
