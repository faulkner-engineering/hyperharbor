using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Power;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>Grants and revokes a host account's access to VM consoles.</summary>
public interface IConsoleAccessGranter
{
    /// <summary>Lets <paramref name="trustee"/> ("HOST\name") open the VM's console. Granting again is harmless.</summary>
    Task GrantAsync(Guid vmId, string trustee, CancellationToken cancellationToken);

    /// <summary>Removes <paramref name="trustee"/>'s console access from every VM on the host.</summary>
    Task RevokeEverywhereAsync(string trustee, CancellationToken cancellationToken);
}

/// <summary>
/// Msvm_TerminalService.GrantInteractiveSessionAccess and RevokeInteractiveSessionAccess. They work
/// unelevated for a member of Hyper-V Administrators, and give a standard account console access to
/// one VM without any other Hyper-V rights.
/// </summary>
public sealed class CimConsoleAccess : IConsoleAccessGranter
{
    public Task GrantAsync(Guid vmId, string trustee, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")
                ?? throw new VmNotFoundException(vmId);
            await InvokeAsync(session, "GrantInteractiveSessionAccess", system, trustee, "console access grant", cancellationToken).ConfigureAwait(false);
        }, cancellationToken);

    public Task RevokeEverywhereAsync(string trustee, CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(async session =>
        {
            foreach (var system in HyperVCim.Query(session, "SELECT * FROM Msvm_ComputerSystem WHERE Caption = 'Virtual Machine'"))
            {
                using (system)
                {
                    try
                    {
                        await InvokeAsync(session, "RevokeInteractiveSessionAccess", system, trustee, "console access revoke", cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HyperVOperationException or HyperVJobFailedException)
                    {
                        // The account was not granted on this VM.
                    }
                }
            }
        }, cancellationToken);

    private static async Task InvokeAsync(CimSession session, string method, CimInstance system, string trustee, string operation, CancellationToken cancellationToken)
    {
        using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_TerminalService")
            ?? throw new HyperVUnavailableException("The Hyper-V terminal service was not found. Enable the Hyper-V role on this host.");
        using var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("ComputerSystem", system, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create("Trustees", new[] { trustee }, CimType.StringArray, CimFlags.In),
        };
        using var result = await HyperVCim.InvokeAsync(session, service, method, parameters, operation, cancellationToken).ConfigureAwait(false);
    }
}
