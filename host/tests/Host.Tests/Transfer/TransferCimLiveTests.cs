using System.Diagnostics;
using System.Text;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Management.Infrastructure;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>
/// Phase 24.4: the transfer's Hyper-V and key protector classes against the real Hyper-V, on one PC playing both hosts.
/// Creates HyperHarbor-Test with a TPM owned by this host's own guardian (as VM creation does), previews it, grants a
/// throwaway guardian through <see cref="KeyProtectorHandover"/> while exporting, then imports a move (same ID, static
/// MAC) and a copy (new ID, dynamic MAC) through <see cref="TransferPreflight"/> plans, and deletes a third import
/// while it is still planned. The host's own guardian is checked before and after and never changed.
/// Changes the host, so it runs only when HH_TRANSFER_CIM_LIVE is set, and needs an elevated shell.
/// HH_TRANSFER_CIM_LIVE=1 dotnet test --filter TransferCimLiveTests --logger "console;verbosity=detailed"
/// </summary>
public sealed class TransferCimLiveTests(ITestOutputHelper output)
{
    private const string SourceName = "HyperHarbor-Test";
    private const string PeerGuardian = "HyperHarbor Live Peer";

    private readonly CimHyperVPowerInvoker _power = new();
    private readonly CimHyperVTransfer _transfer = new();
    private readonly CimKeyProtectorAccess _keys = new();
    private readonly List<Guid> _vms = [];

    [EnvironmentFact("HH_TRANSFER_CIM_LIVE")]
    public async Task MoveAndCopy_ThroughTheProductClasses()
    {
        var baseFolder = Environment.GetEnvironmentVariable("HH_HARNESS_VM_FOLDER") is { Length: > 0 } configured ? configured : Path.GetTempPath();
        var root = Path.Combine(baseFolder, "hh-transfer-cim-" + Guid.NewGuid().ToString("N")[..8]);
        output.WriteLine($"Root: {root}");
        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        Assert.DoesNotContain(await inventory.ListAsync(CancellationToken.None), vm => vm.Name.StartsWith(SourceName, StringComparison.OrdinalIgnoreCase));

        // Creates the host's guardian if it has none yet, as Hyper-V Manager does for the first VM with a TPM.
        await _keys.ExportLocalGuardianAsync(CancellationToken.None);
        var localGuardianBefore = await PowerShellAsync("$g = Get-HgsGuardian -Name UntrustedGuardian; 'LOCAL ' + $g.HasPrivateSigningKey + ' ' + $g.EncryptionCertificate.Thumbprint");

        try
        {
            await RunAsync(root, inventory);
        }
        finally
        {
            await CleanUpAsync(root);
        }

        Assert.Equal(localGuardianBefore, await PowerShellAsync("$g = Get-HgsGuardian -Name UntrustedGuardian; 'LOCAL ' + $g.HasPrivateSigningKey + ' ' + $g.EncryptionCertificate.Thumbprint"));
        output.WriteLine($"This host's guardian is unchanged: {localGuardianBefore.Trim()}");
    }

    private async Task RunAsync(string root, VmInventory inventory)
    {
        var ct = CancellationToken.None;
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        var sourceDisk = Path.Combine(source, "Virtual Hard Disks", SourceName + ".vhdx");
        var iso = Path.Combine(source, "placeholder.iso");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(target);
        Core.Unattend.SeedIso.Write(iso, "LIVE", new Dictionary<string, string> { ["readme.txt"] = "HyperHarbor transfer live test" });

        // 1. The source VM as VM creation makes it, started once so it has TPM state and a MAC address.
        var builder = new CimHyperVBuilder();
        await builder.CreateDiskAsync(sourceDisk, 1L << 30, _ => { }, ct);
        var blueprint = new VmBlueprint(SourceName, source, sourceDisk, iso, 1, 512, 512, false, CimHyperVHost.DefaultSwitchId);
        var vmId = await builder.DefineAsync(blueprint, string.Empty, ct);
        _vms.Add(vmId);
        await builder.ConfigureAsync(vmId, blueprint, ct);
        await builder.EnableTpmAsync(vmId, ct);
        await StartThenTurnOffAsync(vmId, "source");

        // 2. The source's preview, read through the real classes.
        var storage = new CimHyperVStorage();
        var files = new WindowsDiskFiles();
        var versions = new CimHyperVVersions();
        var preview = await new TransferPreviewService(inventory, new CimHyperVDefinitionReader(new CimHyperVCompute()), storage, files, versions)
            .PreviewAsync(vmId, ct);
        output.WriteLine($"Preview: version {preview.ConfigurationVersion}, TPM {preview.TpmEnabled}, {preview.Disks.Count} disk(s) {preview.TotalBytes} bytes, "
            + $"adapters {string.Join(", ", preview.Adapters.Select(adapter => $"{adapter.SwitchName}/{adapter.MacAddress}"))}; "
            + $"blockers [{string.Join(" | ", preview.Blockers.Select(blocker => blocker.Message))}]; warnings [{string.Join(" | ", preview.Warnings)}]");
        Assert.Empty(preview.Blockers);
        Assert.True(preview.TpmEnabled);
        var adapter = Assert.Single(preview.Adapters);
        Assert.Equal(CimHyperVHost.DefaultSwitchId, adapter.SwitchId, ignoreCase: true);
        Assert.Equal("Default Switch", adapter.SwitchName);
        var mac = adapter.MacAddress;
        var capabilities = await versions.ReadCapabilitiesAsync(ct);
        output.WriteLine($"Supported here: {string.Join(", ", capabilities.SupportedVersions)}");
        Assert.True(capabilities.Supports(preview.ConfigurationVersion!));

        // 3. The local guardian export a target would send: public certificates only.
        var localExport = await _keys.ExportLocalGuardianAsync(ct);
        output.WriteLine($"Local guardian export: {localExport.Length} bytes");
        Assert.DoesNotContain("PRIVATE", Encoding.UTF8.GetString(localExport), StringComparison.OrdinalIgnoreCase);

        // 4. A throwaway guardian plays the peer host: granted for the exports, then taken away again.
        var peerFile = Path.Combine(root, "peer.xml");
        await PowerShellAsync($"$p = New-HgsGuardian -Name '{PeerGuardian}' -GenerateCertificates; Export-HgsGuardian -InputObject $p -Path '{peerFile}'");
        var peerExport = await File.ReadAllBytesAsync(peerFile, ct);
        var peerName = KeyProtectorHandover.PeerGuardianName(Guid.NewGuid());
        var original = await _keys.GetAsync(vmId, ct);
        var handover = new KeyProtectorHandover(_keys, NullLogger<KeyProtectorHandover>.Instance);
        var (moveExport, copyExport, discardExport) = await handover.WithPeerGrantedAsync(vmId, peerExport, peerName, async token =>
        {
            var granted = await _keys.GetAsync(vmId, token);
            output.WriteLine($"Key protector: {original.Length} bytes; granted: {granted.Length} bytes");
            Assert.False(granted.AsSpan().SequenceEqual(original));
            return (await _transfer.ExportAsync(vmId, Path.Combine(root, "staging-move"), token),
                await _transfer.ExportAsync(vmId, Path.Combine(root, "staging-copy"), token),
                await _transfer.ExportAsync(vmId, Path.Combine(root, "staging-discard"), token));
        }, ct);
        Assert.Equal(original, await _keys.GetAsync(vmId, ct));
        Assert.Contains("NONE", await PowerShellAsync($"if (Get-HgsGuardian -Name '{peerName}' -ErrorAction SilentlyContinue) {{ 'STILL THERE' }} else {{ 'NONE' }}"), StringComparison.Ordinal);
        output.WriteLine("Original key protector back; the peer's guardian entry is gone.");
        foreach (var file in moveExport.Files)
        {
            output.WriteLine($"  exported {Path.GetRelativePath(root, file)} ({new FileInfo(file).Length} bytes)");
        }

        Assert.DoesNotContain(moveExport.Files, file => DiskPaths.IsDisk(file));

        // 5. The source goes away; the target "receives" the disk into the planned folder.
        await storage.DeleteVmAsync(vmId, ct);
        var vms = await inventory.ListAsync(ct);
        var movePlan = TransferPreflight.Evaluate(preview, TransferMode.Move, Facts(vms, target, capabilities, files));
        output.WriteLine($"Move plan: {movePlan.Name} in {movePlan.VmFolder}; errors [{string.Join(" | ", movePlan.Errors.Select(e => e.Message))}]; warnings [{string.Join(" | ", movePlan.Warnings.Select(w => w.Message))}]");
        Assert.True(movePlan.CanAccept);
        Assert.DoesNotContain(movePlan.Warnings, warning => warning.Field == "network");
        var movedDisk = Assert.Single(movePlan.Disks).TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(movedDisk)!);
        File.Copy(sourceDisk, movedDisk);

        // 6. The move: same ID, the MAC kept as a static address, automatic start off until the transfer completes.
        var moved = await ImportAsync(moveExport, movePlan, newIdentity: false);
        Assert.Equal(vmId, moved);
        await CheckRealizedAsync(moved, movePlan, mac);
        await StartThenTurnOffAsync(moved, "move");

        // 7. The copy: a new ID, "(copy)" in its name since the move now has the name, and a new dynamic MAC.
        vms = await inventory.ListAsync(ct);
        var copyPlan = TransferPreflight.Evaluate(preview, TransferMode.Copy, Facts(vms, target, capabilities, files));
        output.WriteLine($"Copy plan: {copyPlan.Name} in {copyPlan.VmFolder}");
        Assert.Equal(SourceName + " (copy)", copyPlan.Name);
        var copiedDisk = Assert.Single(copyPlan.Disks).TargetPath;
        Directory.CreateDirectory(Path.GetDirectoryName(copiedDisk)!);
        File.Copy(sourceDisk, copiedDisk);
        var copied = await ImportAsync(copyExport, copyPlan, newIdentity: true);
        Assert.NotEqual(vmId, copied);
        await StartThenTurnOffAsync(copied, "copy");
        var copyMac = await CheckRealizedAsync(copied, copyPlan, expectedMac: null);
        Assert.NotEqual(mac, copyMac);

        // 8. A planned system can be thrown away before it is realized.
        var discard = await _transfer.ImportPlannedAsync(discardExport.DefinitionFile, newIdentity: true, ct);
        _vms.Add(discard);
        await _transfer.DeletePlannedAsync(discard, ct);
        Assert.Null(await QueryNameAsync("Msvm_PlannedComputerSystem", discard));
        await _transfer.DeletePlannedAsync(discard, ct);
        output.WriteLine("A planned system was deleted, and deleting it again did nothing.");
    }

    private async Task<Guid> ImportAsync(ExportedDefinition exported, TransferPlan plan, bool newIdentity)
    {
        var ct = CancellationToken.None;
        var plannedId = await _transfer.ImportPlannedAsync(exported.DefinitionFile, newIdentity, ct);
        _vms.Add(plannedId);
        var startup = await _transfer.FixPlannedAsync(plannedId, plan, ct);
        output.WriteLine($"Planned {plannedId}: original automatic start action {startup}");
        await _transfer.ValidatePlannedAsync(plannedId, ct);
        await _transfer.RealizePlannedAsync(plannedId, ct);

        // The VM must not depend on the exported files.
        Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exported.DefinitionFile)!)!)!, recursive: true);

        Assert.Equal(CimHyperVTransfer.StartupActionNone, await StartupActionAsync(plannedId));
        await _transfer.SetAutomaticStartupActionAsync(plannedId, startup, ct);
        Assert.Equal(startup, await StartupActionAsync(plannedId));
        return plannedId;
    }

    /// <summary>Checks name, folders, disk, DVD, and network on the realized VM; returns its MAC address.</summary>
    private Task<string?> CheckRealizedAsync(Guid vmId, TransferPlan plan, string? expectedMac) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            Assert.Equal(plan.Name, settings.CimInstanceProperties["ElementName"].Value);
            foreach (var root in new[] { "ConfigurationDataRoot", "SnapshotDataRoot", "SwapFileDataRoot", "GuestStateDataRoot" })
            {
                Assert.Equal(plan.VmFolder, settings.CimInstanceProperties[root].Value as string, ignoreCase: true);
            }

            var storage = CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData");
            var disks = storage.Where(item => item.CimInstanceProperties["ResourceSubType"]?.Value as string == "Microsoft:Hyper-V:Virtual Hard Disk")
                .Select(item => ((string[])item.CimInstanceProperties["HostResource"].Value)[0]).ToList();
            Assert.Equal(plan.Disks.Select(disk => disk.TargetPath), disks, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain(storage, item => item.CimInstanceProperties["ResourceSubType"]?.Value as string == "Microsoft:Hyper-V:Virtual CD/DVD Disk");

            var port = CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData").Single();
            var connection = CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData").Single();
            Assert.Contains(CimHyperVHost.DefaultSwitchId, ((string[])connection.CimInstanceProperties["HostResource"].Value)[0], StringComparison.OrdinalIgnoreCase);
            var address = port.CimInstanceProperties["Address"]?.Value as string;
            var isStatic = port.CimInstanceProperties["StaticMacAddress"]?.Value is true;
            output.WriteLine($"Realized {plan.Name}: MAC {address} (static {isStatic}), disk {disks[0]}");
            if (expectedMac is not null)
            {
                Assert.True(isStatic);
                Assert.Equal(expectedMac, address);
            }
            else
            {
                Assert.False(isStatic);
            }

            return Task.FromResult(address);
        }, CancellationToken.None);

    private static TransferTargetFacts Facts(IReadOnlyList<Vm> vms, string root, HostVersionCapabilities capabilities, IDiskFiles files) => new(
        vms.Select(vm => (vm.Id, vm.Name)).ToList(),
        root,
        Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().ToList(),
        files.AvailableSpaceMb(root),
        new WindowsHostCapacityReader().Read(),
        0,
        capabilities,
        [new(CimHyperVHost.DefaultSwitchId, "Default Switch", true)]);

    private static Task<ushort?> StartupActionAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            return Task.FromResult(settings.CimInstanceProperties["AutomaticStartupAction"]?.Value as ushort?);
        }, CancellationToken.None);

    private static Task<string?> QueryNameAsync(string className, Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var found = HyperVCim.QuerySingle(session, $"SELECT Name FROM {className} WHERE Name = '{vmId:D}'");
            return Task.FromResult(found?.CimInstanceProperties["Name"]?.Value as string);
        }, CancellationToken.None);

    /// <summary>Starting proves the guest state (and its vTPM) opened; a VM without an OS sits at the firmware.</summary>
    private async Task StartThenTurnOffAsync(Guid vmId, string label)
    {
        await _power.InvokeAsync(vmId, VmAction.Start, CancellationToken.None);
        output.WriteLine($"Started ({label}).");
        await Task.Delay(TimeSpan.FromSeconds(5));
        await TurnOffAsync(vmId);
    }

    private async Task TurnOffAsync(Guid vmId)
    {
        await _power.InvokeAsync(vmId, VmAction.TurnOff, CancellationToken.None);
        for (var attempt = 0; attempt < 60 && await EnabledStateAsync(vmId) is not (3 or null); attempt++)
        {
            await Task.Delay(500);
        }
    }

    private static Task<ushort?> EnabledStateAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var system = HyperVCim.QuerySingle(session, $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'");
            return Task.FromResult(system?.CimInstanceProperties["EnabledState"]?.Value as ushort?);
        }, CancellationToken.None);

    private async Task CleanUpAsync(string root)
    {
        foreach (var vmId in _vms.Distinct())
        {
            try
            {
                if (await EnabledStateAsync(vmId) is { } state && state != 3)
                {
                    await TurnOffAsync(vmId);
                }

                await DestroyAsync(vmId);
            }
            catch (Exception ex)
            {
                output.WriteLine($"Clean-up of VM {vmId} failed: {ex.Message}");
            }
        }

        try
        {
            // Only the throwaway guardian's own certificates (by its name in their subject); never the host's.
            output.WriteLine(await PowerShellAsync($$"""
                Remove-HgsGuardian -Name '{{PeerGuardian}}' -ErrorAction SilentlyContinue
                Get-HgsGuardian | Where-Object Name -like 'HyperHarbor peer *' | ForEach-Object { 'LEFT OVER ' + $_.Name }
                Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like '*({{PeerGuardian}})*' |
                    ForEach-Object { 'REMOVED ' + $_.Subject; Remove-Item $_.PSPath }
                """));
        }
        catch (Exception ex)
        {
            output.WriteLine($"Clean-up of the throwaway guardian failed: {ex.Message}");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Task DestroyAsync(Guid vmId) =>
        HyperVCim.RunAsync(async session =>
        {
            foreach (var className in new[] { "Msvm_ComputerSystem", "Msvm_PlannedComputerSystem" })
            {
                using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM {className} WHERE Name = '{vmId:D}'");
                if (system is null)
                {
                    continue;
                }

                using var service = HyperVCim.ManagementService(session);
                var parameters = new CimMethodParametersCollection { CimMethodParameter.Create("AffectedSystem", system, CimType.Reference, CimFlags.In) };
                await HyperVCim.InvokeAsync(session, service, "DestroySystem", parameters, "DestroySystem", CancellationToken.None);
            }
        }, CancellationToken.None);

    /// <summary>Runs Windows PowerShell with the HgsClient module; the script stops on the first error.</summary>
    private static async Task<string> PowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return process.ExitCode == 0
            ? await stdout
            : throw new InvalidOperationException($"PowerShell failed ({process.ExitCode}): {await stderr}\n{await stdout}");
    }
}
