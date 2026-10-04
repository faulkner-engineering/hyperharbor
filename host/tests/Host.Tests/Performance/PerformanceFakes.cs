using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Tests.Performance;

/// <summary>Records Performance mode calls and keeps whether each VM has a GPU partition.</summary>
internal sealed class FakeHyperVPerformance : IHyperVPerformance
{
    public List<string> Calls { get; } = [];

    public Dictionary<Guid, PerformancePlan> Applied { get; } = [];

    public List<(Guid VmId, string Folder)> Moves { get; } = [];

    public Task<bool> HasGpuPartitionAsync(Guid vmId, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            return Task.FromResult(Applied.ContainsKey(vmId));
        }
    }

    public Task ApplyAsync(Guid vmId, PerformancePlan plan, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add("apply");
            Applied[vmId] = plan;
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid vmId, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add("remove");
            Applied.Remove(vmId);
        }

        return Task.CompletedTask;
    }

    public Task MoveStorageAsync(Guid vmId, string folder, Action<int> progress, CancellationToken cancellationToken)
    {
        lock (Calls)
        {
            Calls.Add("move");
            Moves.Add((vmId, folder));
        }

        progress(100);
        return Task.CompletedTask;
    }
}

/// <summary>An Intel iGPU that can be partitioned, like this project's development host; tests change the driver version.</summary>
internal sealed class FakeHostGpuReader : IHostGpuReader
{
    public const string IntelPath = @"\\?\PCI#VEN_8086&DEV_9B41&SUBSYS_22BE17AA&REV_02#3&11583659&0&10#{064092b3-625e-43bf-9eb5-dc845897dd59}\GPUPARAV";

    public List<HostGpuInfo> Gpus { get; } =
    [
        new("Intel(R) UHD Graphics", GpuVendor.Intel, "30.0.101.1122", "oem9.inf", @"PCI\VEN_8086&DEV_9B41&SUBSYS_22BE17AA&REV_02\3&11583659&0&10",
            IntelPath, new PartitionCapacity(1_000_000_000, ulong.MaxValue, 1_000_000_000, 1_000_000_000, 32)),
    ];

    public string? DriverVersion
    {
        get => Gpus[0].DriverVersion;
        set => Gpus[0] = Gpus[0] with { DriverVersion = value };
    }

    public Task<IReadOnlyList<HostGpuInfo>> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<HostGpuInfo>>(Gpus.ToList());
}
