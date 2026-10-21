using System.Diagnostics;
using System.Text;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Management.Infrastructure;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>
/// Spike for VM transfer (Phase 24.1). On one PC it plays both hosts:
/// <list type="number">
/// <item>Creates HyperHarbor-Test with a vTPM whose key protector is owned by a throwaway guardian A (the "source").</item>
/// <item>Grants a second guardian B on that key protector, imported from a public-only export (the "target").</item>
/// <item>Exports the VM's definition without its storage, then puts the original key protector back.</item>
/// <item>Removes guardian A's private keys, so only B can open the VM's guest state.</item>
/// <item>Imports a move (same ID, static MAC) and a copy (new ID, HyperHarbor-Test2) into their own folders, fixes
/// the planned systems up, validates, realizes, deletes the staging files, and starts each VM.</item>
/// </list>
/// Changes the host (VMs, guardians, certificates), so it runs only when HH_TRANSFER_SPIKE_LIVE is set, and needs an
/// elevated shell (the HGS namespace denies unelevated callers). Everything it creates is removed afterwards; the
/// host's own UntrustedGuardian is never touched. HH_HARNESS_VM_FOLDER chooses where the files go (default: temp).
/// HH_TRANSFER_SPIKE_LIVE=1 dotnet test --filter TransferSpikeLiveTests --logger "console;verbosity=detailed"
/// </summary>
public sealed class TransferSpikeLiveTests(ITestOutputHelper output)
{
    private const string SourceName = "HyperHarbor-Test";
    private const string CopyName = "HyperHarbor-Test2";
    private const string GuardianA = "HyperHarbor Spike A";
    private const string GuardianB = "HyperHarbor Spike B";
    private const string GuardianBPublic = "HyperHarbor Spike B public";
    private const string PlannedType = "Microsoft:Hyper-V:System:Planned";

    private static readonly string[] DataRoots =
        ["ConfigurationDataRoot", "SnapshotDataRoot", "SwapFileDataRoot", "SuspendDataRoot", "LogDataRoot", "GuestStateDataRoot"];

    private readonly CimHyperVPowerInvoker _power = new();
    private readonly List<Guid> _createdVms = [];

    [EnvironmentFact("HH_TRANSFER_SPIKE_LIVE")]
    public async Task ExportWithoutStorage_ImportsAMoveAndACopy_OpenedByAGrantedGuardian()
    {
        var baseFolder = Environment.GetEnvironmentVariable("HH_HARNESS_VM_FOLDER") is { Length: > 0 } configured ? configured : Path.GetTempPath();
        var root = Path.Combine(baseFolder, "hh-transfer-spike-" + Guid.NewGuid().ToString("N")[..8]);
        output.WriteLine($"Root: {root}");
        await AssertNoVmNamedAsync(SourceName);
        await AssertNoVmNamedAsync(CopyName);

        try
        {
            await RunAsync(root);
        }
        finally
        {
            await CleanUpAsync(root);
        }
    }

    private async Task RunAsync(string root)
    {
        var ct = CancellationToken.None;
        var source = Path.Combine(root, "source");
        var sourceDisk = Path.Combine(source, "Virtual Hard Disks", SourceName + ".vhdx");
        var iso = Path.Combine(source, "placeholder.iso");
        Directory.CreateDirectory(source);
        // A real (tiny) image: Hyper-V refuses to start a VM whose DVD holds a file that is not one.
        Core.Unattend.SeedIso.Write(iso, "SPIKE", new Dictionary<string, string> { ["readme.txt"] = "HyperHarbor transfer spike" });

        // 1. The source VM, with a key protector owned by guardian A, started once so it has TPM state and a dynamic MAC.
        var builder = new CimHyperVBuilder();
        await builder.CreateDiskAsync(sourceDisk, 1L << 30, _ => { }, ct);
        var switchId = await DefaultSwitchAsync();
        output.WriteLine($"Default Switch: {switchId ?? "(none; the MAC checks are skipped)"}");
        var blueprint = new VmBlueprint(SourceName, source, sourceDisk, iso, 1, 512, 512, false, switchId);
        var vmId = await builder.DefineAsync(blueprint, string.Empty, ct);
        _createdVms.Add(vmId);
        await builder.ConfigureAsync(vmId, blueprint, ct);
        output.WriteLine($"Source VM {vmId}, config version {await ConfigVersionAsync(vmId)}");
        output.WriteLine($"Supported config versions here: {string.Join(", ", await SupportedVersionsAsync())}");

        var guardianBFile = Path.Combine(root, "guardian-b.xml");
        var setup = await PowerShellAsync($$"""
            $a = New-HgsGuardian -Name '{{GuardianA}}' -GenerateCertificates
            $b = New-HgsGuardian -Name '{{GuardianB}}' -GenerateCertificates
            Export-HgsGuardian -InputObject $b -Path '{{guardianBFile}}'
            $kp = New-HgsKeyProtector -Owner $a -AllowUntrustedRoot
            'KP ' + [Convert]::ToBase64String($kp.RawData)
            """);
        var ownedByA = Convert.FromBase64String(Line(setup, "KP "));
        output.WriteLine($"Guardian B export (public only): {new FileInfo(guardianBFile).Length} bytes; private key in it: "
            + (await File.ReadAllTextAsync(guardianBFile, ct)).Contains("PRIVATE", StringComparison.OrdinalIgnoreCase));

        await SetKeyProtectorAsync(vmId, ownedByA, enableTpm: true);
        await StartThenTurnOffAsync(vmId, "source, first start");
        var mac = await MacAddressAsync(vmId);
        output.WriteLine($"Source MAC after first start: {mac ?? "(no adapter)"}");

        // 2. Grant guardian B (public certificates only) on the VM's key protector.
        // Hyper-V rewrites the key protector it was given (seen live: it differs from ownedByA after the first start).
        var original = await GetKeyProtectorAsync(vmId);
        output.WriteLine($"Key protector set: {ownedByA.Length} bytes; read back after the first start: {original.Length} bytes; "
            + $"identical: {ownedByA.AsSpan().SequenceEqual(original)}; first difference at {FirstDifference(ownedByA, original)}");
        var granted = await PowerShellAsync($$"""
            try { $g = Import-HgsGuardian -Path '{{guardianBFile}}' -Name '{{GuardianBPublic}}' -AllowUntrustedRoot }
            catch { 'IMPORT-CONFLICT ' + $_; Remove-HgsGuardian -Name '{{GuardianB}}'; $g = Import-HgsGuardian -Path '{{guardianBFile}}' -Name '{{GuardianBPublic}}' -AllowUntrustedRoot }
            'GUARDIAN-PRIVATE-KEYS ' + $g.HasPrivateSigningKey
            $kp = ConvertTo-HgsKeyProtector -Bytes ([Convert]::FromBase64String('{{Convert.ToBase64String(original)}}'))
            $new = Grant-HgsKeyProtectorAccess -KeyProtector $kp -Guardian $g -AllowUntrustedRoot
            if ($null -eq $new) { 'GRANT-RETURNED-NOTHING (using the input object)'; $new = $kp }
            $new | Format-List * | Out-String -Width 200
            'KP ' + [Convert]::ToBase64String($new.RawData)
            """);
        await SetKeyProtectorAsync(vmId, Convert.FromBase64String(Line(granted, "KP ")), enableTpm: false);
        output.WriteLine("SetKeyProtector accepted the granted key protector on a VM with TPM state.");

        // 3. Export without storage, twice (one for the move, one for the copy), then restore the original key protector.
        var moveStaging = Path.Combine(root, "staging-move");
        var copyStaging = Path.Combine(root, "staging-copy");
        try
        {
            await ExportAsync(vmId, moveStaging);
            await ExportAsync(vmId, copyStaging);
        }
        finally
        {
            await SetKeyProtectorAsync(vmId, original, enableTpm: false);
        }

        Assert.Equal(original, await GetKeyProtectorAsync(vmId));
        output.WriteLine("The original key protector is back on the source VM.");
        ListFiles("Exported (move)", moveStaging);
        Assert.Empty(Directory.GetFiles(moveStaging, "*.vhd*", SearchOption.AllDirectories));

        // 4. The source goes away (its disk stays), and guardian A's private keys with it: only B can open the guest state.
        await DestroyAsync(vmId);
        var moveFolder = Path.Combine(root, "move");
        var copyFolder = Path.Combine(root, "copy");
        var moveDisk = Path.Combine(moveFolder, "Virtual Hard Disks", SourceName + ".vhdx");
        var copyDisk = Path.Combine(copyFolder, "Virtual Hard Disks", CopyName + ".vhdx");
        Directory.CreateDirectory(Path.GetDirectoryName(moveDisk)!);
        Directory.CreateDirectory(Path.GetDirectoryName(copyDisk)!);
        File.Copy(sourceDisk, copyDisk);
        File.Move(sourceDisk, moveDisk);
        output.WriteLine(await PowerShellAsync($$"""
            $certs = Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like '*({{GuardianA}})*'
            $certs | ForEach-Object { 'REMOVED ' + $_.Subject; Remove-Item $_.PSPath }
            Remove-HgsGuardian -Name '{{GuardianA}}'
            """));

        // 5. The move: same ID, the recorded MAC made static.
        var moved = await ImportAsync(moveStaging, moveFolder, moveDisk, SourceName, newIdentity: false, mac);
        Assert.Equal(vmId, moved);
        await StartThenTurnOffAsync(moved, "move");
        if (mac is not null)
        {
            Assert.Equal(mac, await MacAddressAsync(moved));
        }

        // 6. The copy: a new ID and a new dynamic MAC.
        var copied = await ImportAsync(copyStaging, copyFolder, copyDisk, CopyName, newIdentity: true, mac: null);
        Assert.NotEqual(vmId, copied);
        await StartThenTurnOffAsync(copied, "copy");
        var copyMac = await MacAddressAsync(copied);
        output.WriteLine($"Copy MAC after first start: {copyMac ?? "(no adapter)"}");
        if (mac is not null)
        {
            Assert.NotEqual(mac, copyMac);
        }
    }

    /// <summary>ImportSystemDefinition, fix-ups on the planned system, ValidatePlannedSystem, RealizePlannedSystem.</summary>
    private async Task<Guid> ImportAsync(string staging, string folder, string disk, string name, bool newIdentity, string? mac)
    {
        var definition = Directory.GetFiles(staging, "*.vmcx", SearchOption.AllDirectories).Single();
        var snapshots = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(definition)!)!, "Snapshots");
        Directory.CreateDirectory(snapshots);

        var plannedId = await HyperVCim.RunAsync(async session =>
        {
            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SystemDefinitionFile", definition, CimType.String, CimFlags.In),
                CimMethodParameter.Create("SnapshotFolder", snapshots, CimType.String, CimFlags.In),
                CimMethodParameter.Create("GenerateNewSystemIdentifier", newIdentity, CimType.Boolean, CimFlags.In),
            };
            using var result = await HyperVCim.InvokeAsync(session, service, "ImportSystemDefinition", parameters, "ImportSystemDefinition", CancellationToken.None);
            if (result.OutParameters["ImportedSystem"]?.Value is CimInstance imported && Guid.TryParse(imported.CimInstanceProperties["Name"]?.Value as string, out var id))
            {
                return id;
            }

            // The job form: find the planned system by its name.
            using var byName = HyperVCim.QuerySingle(session, $"SELECT Name FROM Msvm_PlannedComputerSystem WHERE ElementName = '{SourceName}'")
                ?? throw new InvalidOperationException("ImportSystemDefinition left no planned system.");
            return Guid.Parse((string)byName.CimInstanceProperties["Name"].Value);
        }, CancellationToken.None);
        _createdVms.Add(plannedId);
        output.WriteLine($"Planned system {plannedId} ({(newIdentity ? "new identity" : "same identity")})");

        await HyperVCim.RunAsync(async session =>
        {
            using var settings = Planned(session, plannedId);
            output.WriteLine("Planned data roots: " + string.Join("; ", DataRoots.Select(root => $"{root}={settings.CimInstanceProperties[root]?.Value ?? "(missing)"}")));
            output.WriteLine($"Planned GuestStateFile: {settings.CimInstanceProperties["GuestStateFile"]?.Value ?? "(missing)"}");

            // A planned system refuses GuestStateDataRoot (0x80041008, seen live); it follows ConfigurationDataRoot on realize.
            var changes = DataRoots.Where(root => root != "GuestStateDataRoot" && settings.CimInstanceProperties[root] is not null)
                .Select(root => new CimXml.Property(root, CimType.String, folder))
                .Append(new CimXml.Property("ElementName", CimType.String, name))
                .Append(new CimXml.Property("AutomaticStartupAction", CimType.UInt16, (ushort)2))
                .ToArray();
            await CimVmSettings.ModifySystemAsync(session, settings, CancellationToken.None, changes);

            foreach (var storage in CimVmSettings.Associated(session, settings, "Msvm_StorageAllocationSettingData"))
            {
                var subType = storage.CimInstanceProperties["ResourceSubType"]?.Value as string;
                var current = string.Join(", ", storage.CimInstanceProperties["HostResource"]?.Value as string[] ?? []);
                if (subType == "Microsoft:Hyper-V:Virtual Hard Disk")
                {
                    output.WriteLine($"Disk {current} -> {disk}");
                    await CimVmSettings.ModifyResourceAsync(session, storage, CancellationToken.None,
                        new CimXml.Property("HostResource", CimType.StringArray, new[] { disk }));
                }
                else if (subType == "Microsoft:Hyper-V:Virtual CD/DVD Disk")
                {
                    output.WriteLine($"Ejecting {current}");
                    await CimVmSettings.RemoveResourcesAsync(session, [storage], CancellationToken.None);
                }
            }

            foreach (var adapter in CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData"))
            {
                output.WriteLine($"Adapter {adapter.CimInstanceProperties["Address"]?.Value} static={adapter.CimInstanceProperties["StaticMacAddress"]?.Value}");
                CimXml.Property[] macChanges = mac is null
                    ? [new("StaticMacAddress", CimType.Boolean, false), new("Address", CimType.String, string.Empty)]
                    : [new("StaticMacAddress", CimType.Boolean, true), new("Address", CimType.String, mac)];
                await CimVmSettings.ModifyResourceAsync(session, adapter, CancellationToken.None, macChanges);
            }

            foreach (var connection in CimVmSettings.Associated(session, settings, "Msvm_EthernetPortAllocationSettingData"))
            {
                output.WriteLine($"Connection {string.Join(", ", connection.CimInstanceProperties["HostResource"]?.Value as string[] ?? [])}");
            }

            using var service = HyperVCim.ManagementService(session);
            using var planned = PlannedSystem(session, plannedId);
            var validate = new CimMethodParametersCollection { CimMethodParameter.Create("PlannedSystem", planned, CimType.Reference, CimFlags.In) };
            await HyperVCim.InvokeAsync(session, service, "ValidatePlannedSystem", validate, "ValidatePlannedSystem", CancellationToken.None);
            var realize = new CimMethodParametersCollection { CimMethodParameter.Create("PlannedSystem", planned, CimType.Reference, CimFlags.In) };
            await HyperVCim.InvokeAsync(session, service, "RealizePlannedSystem", realize, "RealizePlannedSystem", CancellationToken.None);
        }, CancellationToken.None);

        await HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, plannedId);
            output.WriteLine("Realized data roots: " + string.Join("; ", DataRoots.Select(root => $"{root}={settings.CimInstanceProperties[root]?.Value ?? "(missing)"}")));
            output.WriteLine($"Realized GuestStateFile: {settings.CimInstanceProperties["GuestStateFile"]?.Value ?? "(missing)"}");
            Assert.Equal(name, settings.CimInstanceProperties["ElementName"].Value);
            Assert.Equal((ushort)2, settings.CimInstanceProperties["AutomaticStartupAction"].Value);
            foreach (var root in new[] { "ConfigurationDataRoot", "SnapshotDataRoot", "SwapFileDataRoot", "GuestStateDataRoot" })
            {
                Assert.Equal(folder, settings.CimInstanceProperties[root].Value as string, ignoreCase: true);
            }

            return Task.CompletedTask;
        }, CancellationToken.None);

        // The VM must not depend on the staging files.
        Directory.Delete(staging, recursive: true);
        ListFiles($"Target folder ({name})", folder);
        return plannedId;
    }

    private async Task ExportAsync(Guid vmId, string folder) =>
        await HyperVCim.RunAsync(async session =>
        {
            Directory.CreateDirectory(folder);
            using var system = HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'")!;
            using var template = session.EnumerateAssociatedInstances(HyperVCim.Namespace, system, null, "Msvm_VirtualSystemExportSettingData", null, null).Single();
            var settings = CimXml.Write(template,
                new("CopyVmStorage", CimType.Boolean, false),
                new("CopyVmRuntimeInformation", CimType.Boolean, false),
                new("CopySnapshotConfiguration", CimType.UInt8, (byte)1),
                new("CreateVmExportSubdirectory", CimType.Boolean, true));
            using var service = HyperVCim.ManagementService(session);
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("ComputerSystem", system, CimType.Reference, CimFlags.In),
                CimMethodParameter.Create("ExportDirectory", folder, CimType.String, CimFlags.In),
                CimMethodParameter.Create("ExportSettingData", settings, CimType.String, CimFlags.In),
            };
            await HyperVCim.InvokeAsync(session, service, "ExportSystemDefinition", parameters, "ExportSystemDefinition", CancellationToken.None);
        }, CancellationToken.None);

    private Task<byte[]> GetKeyProtectorAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using var security = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_SecurityService")!;
            var parameters = new CimMethodParametersCollection
            {
                CimMethodParameter.Create("SecuritySettingData", CimXml.Write(security), CimType.String, CimFlags.In),
            };
            using var result = session.InvokeMethod(HyperVCim.Namespace, service, "GetKeyProtector", parameters);
            Assert.Equal(0u, HyperVCim.ReturnCode(result));
            return Task.FromResult((byte[])result.OutParameters["KeyProtector"].Value);
        }, CancellationToken.None);

    private Task SetKeyProtectorAsync(Guid vmId, byte[] keyProtector, bool enableTpm) =>
        HyperVCim.RunAsync(async session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            using var service = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_SecurityService")!;
            using (var security = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData"))
            {
                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("KeyProtector", keyProtector, CimType.UInt8Array, CimFlags.In),
                    CimMethodParameter.Create("SecuritySettingData", CimXml.Write(security), CimType.String, CimFlags.In),
                };
                await HyperVCim.InvokeAsync(session, service, "SetKeyProtector", parameters, "SetKeyProtector", CancellationToken.None);
            }

            if (enableTpm)
            {
                using var updated = CimVmSettings.AssociatedSingle(session, settings, "Msvm_SecuritySettingData");
                var parameters = new CimMethodParametersCollection
                {
                    CimMethodParameter.Create("SecuritySettingData", CimXml.Write(updated, new CimXml.Property("TpmEnabled", CimType.Boolean, true)), CimType.String, CimFlags.In),
                };
                await HyperVCim.InvokeAsync(session, service, "ModifySecuritySettings", parameters, "ModifySecuritySettings", CancellationToken.None);
            }
        }, CancellationToken.None);

    /// <summary>Starting proves the guest state (and its vTPM) opened; a VM without an OS just sits at the firmware.</summary>
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

        // TurnOff can return before the VM is off.
        for (var attempt = 0; attempt < 60 && await EnabledStateAsync(vmId) != 3; attempt++)
        {
            await Task.Delay(500);
        }
    }

    private Task<ushort?> EnabledStateAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var system = HyperVCim.QuerySingle(session, $"SELECT EnabledState FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'");
            return Task.FromResult(system?.CimInstanceProperties["EnabledState"]?.Value as ushort?);
        }, CancellationToken.None);

    private Task<string?> MacAddressAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            var adapter = CimVmSettings.Associated(session, settings, "Msvm_SyntheticEthernetPortSettingData").FirstOrDefault();
            return Task.FromResult(adapter?.CimInstanceProperties["Address"]?.Value as string);
        }, CancellationToken.None);

    private Task<string?> ConfigVersionAsync(Guid vmId) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = CimVmSettings.Realized(session, vmId);
            return Task.FromResult(settings.CimInstanceProperties["Version"]?.Value as string);
        }, CancellationToken.None);

    private static Task<List<string>> SupportedVersionsAsync() =>
        HyperVCim.RunAsync(session =>
        {
            var versions = HyperVCim.Query(session, @"SELECT * FROM Msvm_VirtualSystemSettingData WHERE InstanceID LIKE 'Microsoft:Definition\\VirtualSystem\\Version\\%'");
            return Task.FromResult(versions.Select(version => $"{version.CimInstanceProperties["Version"]?.Value} ({version.CimInstanceProperties["ElementName"]?.Value})").ToList());
        }, CancellationToken.None);

    private static Task<string?> DefaultSwitchAsync() =>
        HyperVCim.RunAsync(session =>
        {
            using var found = HyperVCim.QuerySingle(session, $"SELECT Name FROM Msvm_VirtualEthernetSwitch WHERE Name = '{CimHyperVHost.DefaultSwitchId}'");
            return Task.FromResult(found is null ? null : CimHyperVHost.DefaultSwitchId);
        }, CancellationToken.None);

    private static Task AssertNoVmNamedAsync(string name) =>
        HyperVCim.RunAsync(session =>
        {
            Assert.Empty(HyperVCim.Query(session, $"SELECT Name FROM Msvm_ComputerSystem WHERE ElementName = '{name}'"));
            Assert.Empty(HyperVCim.Query(session, $"SELECT Name FROM Msvm_PlannedComputerSystem WHERE ElementName = '{name}'"));
            return Task.CompletedTask;
        }, CancellationToken.None);

    /// <summary>DestroySystem on a realized or planned VM, if it exists.</summary>
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

    private static CimInstance Planned(CimSession session, Guid vmId) =>
        HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = '{PlannedType}'")
            ?? throw new InvalidOperationException($"No planned settings for {vmId}.");

    private static CimInstance PlannedSystem(CimSession session, Guid vmId) =>
        HyperVCim.QuerySingle(session, $"SELECT * FROM Msvm_PlannedComputerSystem WHERE Name = '{vmId:D}'")
            ?? throw new InvalidOperationException($"No planned system {vmId}.");

    private async Task CleanUpAsync(string root)
    {
        foreach (var vmId in _createdVms.Distinct())
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
            output.WriteLine(await PowerShellAsync($$"""
                foreach ($name in '{{GuardianA}}', '{{GuardianB}}', '{{GuardianBPublic}}') { Remove-HgsGuardian -Name $name -ErrorAction SilentlyContinue }
                Get-ChildItem 'Cert:\LocalMachine\Shielded VM Local Certificates' | Where-Object Subject -like '*(HyperHarbor Spike*' |
                    ForEach-Object { 'REMOVED ' + $_.Subject; Remove-Item $_.PSPath }
                """));
        }
        catch (Exception ex)
        {
            output.WriteLine($"Clean-up of the spike guardians failed: {ex.Message}");
        }

        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void ListFiles(string label, string folder)
    {
        output.WriteLine($"{label}:");
        foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
        {
            output.WriteLine($"  {Path.GetRelativePath(folder, file)} ({new FileInfo(file).Length} bytes)");
        }
    }

    private static int FirstDifference(byte[] left, byte[] right)
    {
        var index = left.AsSpan().CommonPrefixLength(right);
        return index == left.Length && index == right.Length ? -1 : index;
    }

    private static string Line(string text, string prefix) =>
        text.Split('\n').Select(line => line.Trim()).Last(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];

    /// <summary>Runs Windows PowerShell with the HgsClient module; the script stops on the first error.</summary>
    private async Task<string> PowerShellAsync(string script)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // A PowerShell 7 module path breaks Windows PowerShell's own modules (see Live testing in CLAUDE.md).
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"PowerShell failed ({process.ExitCode}): {await stderr}\n{await stdout}");
        }

        return await stdout;
    }
}
