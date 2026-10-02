namespace HyperHarbor.Host.Core.HyperV;

/// <summary>
/// Raw values read from root\virtualization\v2 in a single pass. Mapped to contract types by <see cref="VmMapper"/>.
/// </summary>
public sealed record HyperVSnapshot(
    IReadOnlyList<ComputerSystemRow> ComputerSystems,
    IReadOnlyList<SettingsRow> Settings,
    IReadOnlyList<SummaryRow> Summaries,
    IReadOnlyList<GuestNetworkRow> GuestNetworks);

/// <summary>Values from Msvm_ComputerSystem.</summary>
/// <param name="Id">The Name property, which holds the virtual machine GUID.</param>
/// <param name="Name">The ElementName property, shown as the virtual machine name.</param>
/// <param name="OperationalStatus">First OperationalStatus element. 11 (In Service) indicates a transition.</param>
/// <param name="RequestedState">Target state of an in-progress transition. 5 (No Change) when idle.</param>
/// <param name="OtherEnabledState">Describes the state when EnabledState is 1 (Other), for example "Saving".</param>
public sealed record ComputerSystemRow(
    Guid Id,
    string Name,
    ushort EnabledState,
    ulong OnTimeMilliseconds,
    ushort? OperationalStatus = null,
    ushort RequestedState = ComputerSystemRow.RequestedStateNoChange,
    string? OtherEnabledState = null)
{
    public const ushort RequestedStateNoChange = 5;
}

/// <summary>Values from the realized Msvm_VirtualSystemSettingData.</summary>
/// <param name="SubType">VirtualSystemSubType, for example Microsoft:Hyper-V:SubType:2.</param>
public sealed record SettingsRow(Guid VmId, string? SubType);

/// <summary>Values from Msvm_SummaryInformation returned by GetSummaryInformation.</summary>
public sealed record SummaryRow(Guid VmId, ushort? ProcessorLoad, ulong? MemoryUsageMb);

/// <summary>Values from Msvm_GuestNetworkAdapterConfiguration. Requires the guest data exchange integration service.</summary>
public sealed record GuestNetworkRow(Guid VmId, IReadOnlyList<string> IpAddresses);
