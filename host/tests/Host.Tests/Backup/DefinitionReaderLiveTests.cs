using System.Diagnostics;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Tests.HyperV;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Backup;

/// <summary>
/// Read-only: compares the definition reader's network adapters with Hyper-V's own cmdlet for every VM on this host,
/// including adapters connected to no switch (which the reader once dropped).
/// </summary>
public sealed class DefinitionReaderLiveTests(ITestOutputHelper output)
{
    [HyperVFact]
    public async Task ReadAsync_ListsEveryNetworkAdapter_WithItsSwitch()
    {
        var expected = ReadWithPowerShell();
        var reader = new CimHyperVDefinitionReader(new CimHyperVCompute());

        foreach (var (vmId, adapters) in expected)
        {
            var definition = await reader.ReadAsync(vmId, CancellationToken.None);
            output.WriteLine($"{definition.Name}: {string.Join("; ", definition.Adapters.Select(a => $"{a.SwitchName ?? "(none)"} {a.MacAddress}"))}");

            Assert.Equal(adapters.Count, definition.Adapters.Count);
            Assert.Equal(
                adapters.Select(adapter => adapter.SwitchId).Order(StringComparer.OrdinalIgnoreCase),
                definition.Adapters.Select(adapter => adapter.SwitchId?.ToUpperInvariant()).Order(StringComparer.OrdinalIgnoreCase));
        }
    }

    private static Dictionary<Guid, List<(string? SwitchId, string Mac)>> ReadWithPowerShell()
    {
        const string script =
            "Get-VM | ForEach-Object { $vm = $_; Get-VMNetworkAdapter -VM $vm | ForEach-Object { " +
            "'{0}|{1}|{2}' -f $vm.Id, $_.SwitchId, $_.MacAddress } }";
        var start = new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var result = new Dictionary<Guid, List<(string?, string)>>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            var list = result.TryGetValue(Guid.Parse(parts[0]), out var existing) ? existing : result[Guid.Parse(parts[0])] = [];
            list.Add((parts[1].Length == 0 ? null : parts[1].ToUpperInvariant(), parts[2]));
        }

        // A VM without adapters still has to read as having none.
        var all = Process.Start(new ProcessStartInfo("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Get-VM | ForEach-Object { $_.Id.ToString() }"])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        foreach (var id in all.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.TryAdd(Guid.Parse(id), []);
        }

        all.WaitForExit();
        return result;
    }
}
