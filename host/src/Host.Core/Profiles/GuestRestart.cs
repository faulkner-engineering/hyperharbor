using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// Restarts a Windows guest through its shutdown component and waits until Remote Desktop answers again, for setup
/// profiles whose changes need a restart to finish.
/// </summary>
public sealed class GuestRestart(IVmInventory inventory, IRemoteAccessProbe probe, IHyperVPowerInvoker power, TimeProvider time)
{
    /// <summary>How often the wait checks the guest.</summary>
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Checks before giving up: 60 of 15 seconds, 15 minutes.</summary>
    internal int Checks { get; init; } = 60;

    /// <summary>The longest wait, in words, for problem text.</summary>
    public string Timeout => $"{(int)Math.Round((Poll * Checks).TotalMinutes)} minutes";

    /// <summary>
    /// True once the restarted VM answers Remote Desktop again; false when the guest refused the restart or did not
    /// answer in time. Windows keeps answering for a few seconds after the request, so an answer counts only after one
    /// check found it not answering (or not running); a guest reboot does not reset uptime, so that is the only sign
    /// of the restart.
    /// </summary>
    public async Task<bool> RestartAndWaitAsync(Guid vmId, CancellationToken cancellationToken)
    {
        try
        {
            await power.InvokeAsync(vmId, VmAction.Restart, cancellationToken).ConfigureAwait(false);
        }
        catch (VmActionNotAllowedException)
        {
            // No contact with the guest's shutdown component.
            return false;
        }

        var wentDown = false;
        for (var check = 0; check < Checks; check++)
        {
            await Task.Delay(Poll, time, cancellationToken).ConfigureAwait(false);
            var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false);
            var answers = vm is { State: VmState.Running } && Ipv4(vm) is { } address
                && await probe.RdpAnswersAsync(address, cancellationToken).ConfigureAwait(false);
            if (answers && wentDown)
            {
                return true;
            }

            wentDown |= !answers;
        }

        return false;
    }

    private static string? Ipv4(Vm vm) =>
        vm.IpAddresses.FirstOrDefault(address => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
}
