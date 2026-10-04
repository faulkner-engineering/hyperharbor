using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>
/// Opens VM consoles. The client receives the User's host console account with a freshly rotated
/// password and a ticket for tunnels to the host's Virtual Machine Connection port; the account is
/// granted access to the VM first.
/// </summary>
public sealed class ConsoleService
{
    private readonly IVmInventory _inventory;
    private readonly ConsoleAccountStore _accounts;
    private readonly ConsolePasswordRotator _rotator;
    private readonly IConsoleAccessGranter _access;
    private readonly ConsoleTicketStore _tickets;
    private readonly ILogger<ConsoleService> _logger;

    public ConsoleService(
        IVmInventory inventory,
        ConsoleAccountStore accounts,
        ConsolePasswordRotator rotator,
        IConsoleAccessGranter access,
        ConsoleTicketStore tickets,
        ILogger<ConsoleService> logger)
    {
        _inventory = inventory;
        _accounts = accounts;
        _rotator = rotator;
        _access = access;
        _tickets = tickets;
        _logger = logger;
    }

    /// <summary>The account a host console account signs in as, for example "MYPC\hhc-owner".</summary>
    public static string Trustee(string accountName) => $@"{Environment.MachineName}\{accountName}";

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="ConsoleConflictException">The VM is not running, or console access is not set up.</exception>
    public async Task<ConsoleSession> OpenAsync(Guid vmId, Guid userId, Guid deviceId, string deviceName, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        if (vm.State is not (VmState.Running or VmState.Paused))
        {
            throw new ConsoleConflictException($"{vm.Name} is not running. Start it, then open the console.", ContractInfo.ProblemCodes.VmNotRunning);
        }

        var account = _accounts.Find(userId)
            ?? throw ConsoleConflictException.SetupRequired("This host has no console account for your user yet.");
        var trustee = Trustee(account.AccountName);
        await _access.GrantAsync(vmId, trustee, cancellationToken).ConfigureAwait(false);

        var password = await _rotator.GetPasswordAsync(userId, cancellationToken).ConfigureAwait(false);
        var ticket = _tickets.Issue(deviceId, userId, vmId);
        _logger.LogInformation("Opened a console session for {Account} on {Name} ({VmId}) for {Device}.", account.AccountName, vm.Name, vmId, deviceName);

        return new ConsoleSession(ticket.Value, trustee, password.Password, vmId.ToString("D"), password.ExpiresAt, ticket.ExpiresAt);
    }
}
