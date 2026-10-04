namespace HyperHarbor.Shared.Contracts.Vms;

/// <summary>Credentials and a tunnel ticket for a VM's console. Schema: ConsoleSession.</summary>
/// <param name="Ticket">Opens tunnels with <c>POST /vms/{vmId}/console/tunnel</c> until <paramref name="TicketExpiresAt"/>.</param>
/// <param name="UserName">The host console account, for example "MYPC\hhc-owner".</param>
/// <param name="Password">Valid until the reuse window ends, when the host rotates it again.</param>
/// <param name="Pcb">The Remote Desktop pre-connection blob that selects the VM: its ID.</param>
/// <param name="ExpiresAt">End of the password's reuse window.</param>
public sealed record ConsoleSession(string Ticket, string UserName, string Password, string Pcb, DateTimeOffset ExpiresAt, DateTimeOffset TicketExpiresAt)
{
    /// <summary>Keeps the ticket and password out of logs and exception messages.</summary>
    public override string ToString() =>
        $"ConsoleSession {{ UserName = {UserName}, Pcb = {Pcb}, ExpiresAt = {ExpiresAt:u}, TicketExpiresAt = {TicketExpiresAt:u} }}";
}
