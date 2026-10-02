using System.Net;
using System.Net.Sockets;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Converts raw Hyper-V values into contract <see cref="Vm"/> records.
/// </summary>
public static class VmMapper
{
    private const string Generation2SubType = "Microsoft:Hyper-V:SubType:2";

    public static IReadOnlyList<Vm> Map(HyperVSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var settings = snapshot.Settings
            .GroupBy(row => row.VmId)
            .ToDictionary(group => group.Key, group => group.First());
        var summaries = snapshot.Summaries
            .GroupBy(row => row.VmId)
            .ToDictionary(group => group.Key, group => group.First());
        var addresses = snapshot.GuestNetworks
            .GroupBy(row => row.VmId)
            .ToDictionary(group => group.Key, group => group.SelectMany(row => row.IpAddresses).ToList());

        return snapshot.ComputerSystems
            .Select(system => MapVm(
                system,
                settings.GetValueOrDefault(system.Id),
                summaries.GetValueOrDefault(system.Id),
                addresses.GetValueOrDefault(system.Id) ?? []))
            .OrderBy(vm => vm.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(vm => vm.Id)
            .ToList();
    }

    private const ushort EnabledStateOther = 1;
    private const ushort OperationalStatusInService = 11;

    /// <summary>
    /// Maps a virtual machine's state, including transitions. Observed on Hyper-V:
    /// saving reports EnabledState 1 with OtherEnabledState "Saving"; starting reports
    /// OperationalStatus 11 (In Service) with RequestedState 2 before EnabledState becomes 10.
    /// </summary>
    public static VmState MapState(ComputerSystemRow system)
    {
        ArgumentNullException.ThrowIfNull(system);

        if (system.EnabledState == EnabledStateOther && MapOtherEnabledState(system.OtherEnabledState) is { } described)
        {
            return described;
        }

        if (system.OperationalStatus == OperationalStatusInService
            && MapRequestedState(system.RequestedState, system.EnabledState) is { } target)
        {
            return target;
        }

        return MapState(system.EnabledState);
    }

    private static VmState? MapOtherEnabledState(string? otherEnabledState) => otherEnabledState?.Trim().ToUpperInvariant() switch
    {
        "SAVING" => VmState.Saving,
        "PAUSING" => VmState.Pausing,
        "RESUMING" => VmState.Resuming,
        "STARTING" => VmState.Starting,
        "STOPPING" or "SHUTTING DOWN" => VmState.Stopping,
        _ => null,
    };

    /// <summary>Maps the target of an in-progress RequestStateChange to the transitional state.</summary>
    private static VmState? MapRequestedState(ushort requestedState, ushort enabledState) => requestedState switch
    {
        2 => enabledState == 9 ? VmState.Resuming : VmState.Starting,
        3 or 4 => VmState.Stopping,
        6 => VmState.Saving,
        9 => VmState.Pausing,
        _ => null,
    };

    /// <summary>
    /// Maps Msvm_ComputerSystem.EnabledState. Values 2 through 10 are the CIM values used by
    /// root\virtualization\v2. Values from 32768 are Hyper-V transitional states.
    /// </summary>
    public static VmState MapState(ushort enabledState) => enabledState switch
    {
        2 => VmState.Running,
        3 => VmState.Off,
        4 => VmState.Stopping,
        6 => VmState.Saved,
        9 => VmState.Paused,
        10 => VmState.Starting,
        32768 => VmState.Paused,
        32769 => VmState.Saved,
        32770 => VmState.Starting,
        32773 => VmState.Saving,
        32774 => VmState.Stopping,
        32776 => VmState.Pausing,
        32777 => VmState.Resuming,
        _ => VmState.Other,
    };

    private static Vm MapVm(
        ComputerSystemRow system,
        SettingsRow? settings,
        SummaryRow? summary,
        IReadOnlyList<string> rawAddresses)
    {
        var state = MapState(system);
        var isActive = state is VmState.Running or VmState.Paused;
        var ipAddresses = isActive ? NormalizeAddresses(rawAddresses) : [];

        return new Vm(
            Id: system.Id,
            Name: system.Name,
            State: state,
            UptimeSeconds: isActive ? (long)(system.OnTimeMilliseconds / 1000) : null,
            CpuUsagePercent: state == VmState.Running && summary?.ProcessorLoad is { } load ? Math.Min((int)load, 100) : null,
            MemoryAssignedMb: isActive && summary?.MemoryUsageMb is { } memory ? (long)memory : null,
            Generation: string.Equals(settings?.SubType, Generation2SubType, StringComparison.OrdinalIgnoreCase) ? 2 : 1,
            RdpAvailable: state == VmState.Running && ipAddresses.Count > 0,
            IpAddresses: ipAddresses);
    }

    /// <summary>
    /// Keeps valid, non-loopback, non-link-local addresses, removes duplicates, and lists IPv4 before IPv6.
    /// </summary>
    private static IReadOnlyList<string> NormalizeAddresses(IReadOnlyList<string> rawAddresses)
    {
        return rawAddresses
            .Select(raw => IPAddress.TryParse(raw, out var address) ? address : null)
            .OfType<IPAddress>()
            .Where(address => !IPAddress.IsLoopback(address)
                && !address.IsIPv6LinkLocal
                && !IsIPv4LinkLocal(address))
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .Select(address => address.ToString())
            .ToList();
    }

    private static bool IsIPv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }
}
