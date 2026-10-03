namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>What the host can give new or changed VMs. Schema: HostResources.</summary>
/// <param name="AvailableMemoryMb">Physical memory free right now.</param>
/// <param name="MemoryReserveMb">The host warns when a VM would leave less than this free.</param>
/// <param name="VirtualHardDiskFolder">Where new VMs' disks are created.</param>
/// <param name="IsoFolder">The ISO library folder; GET /isos lists the images in it.</param>
public sealed record HostResources(
    int LogicalProcessorCount,
    long TotalMemoryMb,
    long AvailableMemoryMb,
    long MemoryReserveMb,
    string VirtualHardDiskFolder,
    string IsoFolder);

/// <summary>An installation image in the host's ISO library. Schema: IsoImage.</summary>
/// <param name="Name">Path relative to the library folder, for example "Windows\Win11_24H2.iso".</param>
public sealed record IsoImage(string Name, long SizeBytes, DateTimeOffset ModifiedAt);

/// <summary>A Hyper-V virtual switch. Schema: VirtualSwitch.</summary>
/// <param name="IsDefault">The NAT "Default Switch"; VMs on it are reachable only from the host.</param>
public sealed record VirtualSwitch(string Id, string Name, bool IsDefault);
