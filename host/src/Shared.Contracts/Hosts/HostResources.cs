namespace HyperHarbor.Shared.Contracts.Hosts;

/// <summary>What the host can give new or changed VMs. Schema: HostResources.</summary>
/// <param name="AvailableMemoryMb">Physical memory free right now.</param>
/// <param name="MemoryReserveMb">The host warns when a VM would leave less than this free.</param>
/// <param name="VirtualHardDiskFolder">Where new VMs' disks are created.</param>
/// <param name="IsoFolder">The ISO library folder; GET /isos lists the images in it.</param>
/// <param name="PhysicalCoreCount">Physical cores across all processors (API 1.38.0); null when Windows cannot say.</param>
/// <param name="CpuUsagePercent">Processor use across all logical processors, 0 to 100 (API 1.38.0); null when unread.</param>
public sealed record HostResources(
    int LogicalProcessorCount,
    long TotalMemoryMb,
    long AvailableMemoryMb,
    long MemoryReserveMb,
    string VirtualHardDiskFolder,
    string IsoFolder,
    int? PhysicalCoreCount = null,
    double? CpuUsagePercent = null);

/// <summary>An installation image in the host's ISO library. Schema: IsoImage.</summary>
/// <param name="Name">The file name in the library folder, for example "Win11_24H2.iso".</param>
/// <param name="UsedBy">Names of the VMs (current settings or checkpoints) that have this image attached.</param>
public sealed record IsoImage(string Name, long SizeBytes, DateTimeOffset ModifiedAt, IReadOnlyList<string> UsedBy);

/// <summary>Renames an image in the ISO library. Schema: RenameIsoRequest.</summary>
/// <param name="NewName">A file name ending in .iso.</param>
public sealed record RenameIsoRequest([property: System.Text.Json.Serialization.JsonRequired] string NewName);

/// <summary>A Hyper-V virtual switch. Schema: VirtualSwitch.</summary>
/// <param name="IsDefault">The NAT "Default Switch"; VMs on it are reachable only from the host.</param>
public sealed record VirtualSwitch(string Id, string Name, bool IsDefault);
