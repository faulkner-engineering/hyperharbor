using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;

namespace HyperHarbor.Host.Tests.HyperV;

/// <summary>
/// Read-only checks against the local Hyper-V installation. Skipped when Hyper-V is unavailable.
/// </summary>
public class CimHyperVReaderLiveTests
{
    [HyperVFact]
    public async Task ListAsync_ReturnsVmsWithDistinctIdsAndNames()
    {
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));

        var vms = await inventory.ListAsync(CancellationToken.None);

        Assert.All(vms, vm =>
        {
            Assert.NotEqual(Guid.Empty, vm.Id);
            Assert.False(string.IsNullOrWhiteSpace(vm.Name));
            Assert.InRange(vm.Generation, 1, 2);
        });
        Assert.Equal(vms.Count, vms.Select(vm => vm.Id).Distinct().Count());
    }

    [HyperVFact]
    public async Task ListAsync_RunningVmsReportSummaryMetrics()
    {
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));

        var vms = await inventory.ListAsync(CancellationToken.None);

        Assert.All(vms.Where(vm => vm.State == VmState.Running), vm =>
        {
            Assert.NotNull(vm.UptimeSeconds);
            Assert.NotNull(vm.CpuUsagePercent);
            Assert.NotNull(vm.MemoryAssignedMb);
        });
    }
}
