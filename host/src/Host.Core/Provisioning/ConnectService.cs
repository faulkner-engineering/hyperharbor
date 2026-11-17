using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Issues Remote Desktop credentials for a User's account on a provisioned, running VM.
/// </summary>
public sealed class ConnectService
{
    public const int RemoteDesktopPort = 3389;

    private readonly IVmInventory _inventory;
    private readonly ProvisioningStore _provisioning;
    private readonly PasswordRotator _rotator;
    private readonly ILogger<ConnectService> _logger;
    private readonly Performance.PerformanceStore? _performance;
    private readonly Profiles.VmConnectionDefaultsStore? _defaults;
    private readonly GuestAdminService? _guestAdmin;

    public ConnectService(
        IVmInventory inventory,
        ProvisioningStore provisioning,
        PasswordRotator rotator,
        ILogger<ConnectService> logger,
        Performance.PerformanceStore? performance = null,
        Profiles.VmConnectionDefaultsStore? defaults = null,
        GuestAdminService? guestAdmin = null)
    {
        _guestAdmin = guestAdmin;
        _performance = performance;
        _defaults = defaults;
        _inventory = inventory;
        _provisioning = provisioning;
        _rotator = rotator;
        _logger = logger;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="GuestAccountConflictException">Not provisioned for this User, not running, or no address.</exception>
    /// <exception cref="GuestCredentialRejectedException">
    /// The guest rejected the stored administrator credential and the host holds no earlier password to issue.
    /// </exception>
    public async Task<VmConnection> ConnectAsync(Guid vmId, Guid userId, string deviceName, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken) ?? throw new VmNotFoundException(vmId);
        var account = _provisioning.Find(vmId, userId)
            ?? throw new GuestAccountConflictException($"{vm.Name} is not set up for Remote Desktop yet. Provision it first.");

        if (vm.State != VmState.Running)
        {
            throw new GuestAccountConflictException($"{vm.Name} is not running. Start it, then connect.");
        }

        var address = PreferredAddress(vm)
            ?? throw new GuestAccountConflictException($"{vm.Name} has not reported a network address yet. Wait for it to finish starting.");

        RotatedPassword rotated;
        try
        {
            rotated = await _rotator.GetPasswordAsync(account.ToTarget(address), userId, account.AccountName, cancellationToken);
        }
        catch (GuestCredentialRejectedException ex)
        {
            // Only a connect without an earlier password gets here; others are issued the one set last time (PasswordRotator).
            throw new GuestCredentialRejectedException(
                $"{vm.Name} did not accept the administrator account HyperHarbor has stored for it: its password has expired or was changed in the VM. " +
                $"Choose Administrator account… in the VM's menu to store a working one. ({ex.Message})");
        }

        // So the first User Account Control prompt in the session already offers the User's administrator account.
        if (account.GuestOs != GuestOsFamily.Linux)
        {
            _guestAdmin?.EnsureListedLater(vmId, userId);
        }

        _logger.LogInformation(
            "Issued Remote Desktop credentials for {Account} on {Name} ({VmId}) to {Device}{Note}.",
            account.AccountName,
            vm.Name,
            vmId,
            deviceName,
            rotated.Rotated ? "" : " (not rotated: the guest rejected the stored administrator credential)");

        // The plain account name for both. Given ".\hh-owner", mstsc signs in as ".\.\hh-owner", which
        // the guest rejects as an unknown user (seen on Windows 11). A plain name resolves to the guest's
        // local account, and xrdp expects the plain Linux user name.
        var userName = account.AccountName;
        return new VmConnection(
            userName, rotated.Password, address, RemoteDesktopPort, rotated.ExpiresAt, account.GuestOs, _performance?.Find(vmId) is not null,
            _defaults?.Find(vmId)?.Connection,
            rotated.Rotated);
    }

    /// <summary>The Remote Desktop address reported for the VM, else its first IPv4 address.</summary>
    internal static string? PreferredAddress(Vm vm) =>
        vm.RemoteDesktop?.Address
        ?? vm.IpAddresses.FirstOrDefault(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
        ?? vm.IpAddresses.FirstOrDefault();
}
