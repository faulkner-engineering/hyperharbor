using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>What a partitionable GPU offers each partition, in its own relative units.</summary>
public sealed record PartitionCapacity(ulong Vram, ulong Encode, ulong Decode, ulong Compute, int PartitionCount);

/// <summary>A host GPU with its driver.</summary>
/// <param name="PnpDeviceId">For example "PCI\VEN_8086&amp;DEV_9B41&amp;...\3&amp;11583659&amp;0&amp;10".</param>
/// <param name="DriverInf">The installed driver package, for example "oem9.inf".</param>
/// <param name="InstancePath">Msvm_PartitionableGpu.Name when the GPU can be partitioned.</param>
public sealed record HostGpuInfo(
    string Name,
    GpuVendor Vendor,
    string? DriverVersion,
    string? DriverInf,
    string PnpDeviceId,
    string? InstancePath,
    PartitionCapacity? Capacity)
{
    public bool Partitionable => InstancePath is not null;

    public HostGpuDevice ToContract() => new(Name, Vendor, DriverVersion, Partitionable, Capacity?.PartitionCount ?? 0, InstancePath);
}

public interface IHostGpuReader
{
    Task<IReadOnlyList<HostGpuInfo>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Win32_VideoController (root\cimv2) for the GPUs and their drivers, matched by PnP device ID to
/// Msvm_PartitionableGpu (root\virtualization\v2) for those Hyper-V can partition. Works without
/// administrator rights for a member of Hyper-V Administrators.
/// </summary>
public sealed class CimHostGpuReader : IHostGpuReader
{
    public Task<IReadOnlyList<HostGpuInfo>> ReadAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync<IReadOnlyList<HostGpuInfo>>(session =>
        {
            var partitionable = new Dictionary<string, (string Path, PartitionCapacity Capacity)>(StringComparer.OrdinalIgnoreCase);
            foreach (var gpu in HyperVCim.Query(session, "SELECT * FROM Msvm_PartitionableGpu"))
            {
                using (gpu)
                {
                    var name = (string)gpu.CimInstanceProperties["Name"].Value;
                    if (GpuIdentity.DeviceInstanceId(name) is not { } instance)
                    {
                        continue;
                    }

                    var counts = (gpu.CimInstanceProperties["ValidPartitionCounts"]?.Value as Array)?.Cast<object>().Select(count => (int)GpuIdentity.ToUInt64(count)).ToArray();
                    partitionable[instance] = (name, new PartitionCapacity(
                        GpuIdentity.ToUInt64(gpu.CimInstanceProperties["MaxPartitionVRAM"]?.Value),
                        GpuIdentity.ToUInt64(gpu.CimInstanceProperties["MaxPartitionEncode"]?.Value),
                        GpuIdentity.ToUInt64(gpu.CimInstanceProperties["MaxPartitionDecode"]?.Value),
                        GpuIdentity.ToUInt64(gpu.CimInstanceProperties["MaxPartitionCompute"]?.Value),
                        counts is { Length: > 0 } ? counts.Max() : (int)GpuIdentity.ToUInt64(gpu.CimInstanceProperties["PartitionCount"]?.Value)));
                }
            }

            var gpus = new List<HostGpuInfo>();
            foreach (var controller in session.QueryInstances(@"root\cimv2", HyperVCim.QueryDialect,
                "SELECT Name, PNPDeviceID, DriverVersion, InfFilename FROM Win32_VideoController"))
            {
                using (controller)
                {
                    var pnp = controller.CimInstanceProperties["PNPDeviceID"]?.Value as string;
                    if (pnp is null || !pnp.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    partitionable.TryGetValue(pnp, out var partition);
                    gpus.Add(new HostGpuInfo(
                        controller.CimInstanceProperties["Name"]?.Value as string ?? pnp,
                        GpuIdentity.VendorOf(pnp),
                        controller.CimInstanceProperties["DriverVersion"]?.Value as string,
                        controller.CimInstanceProperties["InfFilename"]?.Value as string,
                        pnp,
                        partition.Path,
                        partition.Capacity));
                }
            }

            return Task.FromResult<IReadOnlyList<HostGpuInfo>>(gpus);
        }, cancellationToken);
}
