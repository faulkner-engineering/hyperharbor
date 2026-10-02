using HyperHarbor.Host.Core.Wake;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Wake;

/// <summary>
/// Read-only check of the real Windows reader. It changes nothing, so it always runs; the
/// assertions hold on any Windows machine. Use --logger "console;verbosity=detailed" to see output.
/// </summary>
public class WindowsWakeEnvironmentReaderLiveTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReadsAdaptersPowerStateAndReadiness()
    {
        var environment = await new WindowsWakeEnvironmentReader().ReadAsync(CancellationToken.None);

        foreach (var adapter in environment.Adapters.Where(a => a.IsPhysical))
        {
            output.WriteLine(
                $"{adapter.Name} ({adapter.Description}) mac={adapter.MacAddress} wired={adapter.IsWired} connected={adapter.IsConnected} " +
                $"magicPacket={adapter.WakeOnMagicPacket?.ToString() ?? "unknown"} ipv4=[{string.Join(", ", adapter.Ipv4Addresses.Select(a => $"{a.Address}/{a.PrefixLength}"))}]");
        }

        output.WriteLine($"Power: {environment.Power}");
        output.WriteLine($"Wake-armed: {string.Join("; ", environment.WakeArmedDevices)}");
        foreach (var check in WakeReadinessEvaluator.Evaluate(environment).Checks)
        {
            output.WriteLine($"[{check.Status}] {check.Id}: {check.Detail}");
        }

        Assert.Contains(environment.Adapters, adapter => adapter.IsPhysical);
        Assert.All(environment.Adapters, adapter => Assert.Equal(12, adapter.MacAddress.Length));
    }
}
