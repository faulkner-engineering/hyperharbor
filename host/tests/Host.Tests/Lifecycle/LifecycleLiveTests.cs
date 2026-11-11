using System.Diagnostics;
using HyperHarbor.Host.Core;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Management.Infrastructure;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>
/// Creates a throwaway VM named HyperHarbor-Test through the real services, checks its settings in
/// Hyper-V, moves it to a throwaway private switch and back, then deletes it with its disk and the switch.
/// Changes the host, so it runs only when HH_LIFECYCLE_LIVE is set:
/// HH_LIFECYCLE_LIVE=1 dotnet test --filter LifecycleLiveTests
/// </summary>
public sealed class LifecycleLiveTests(ITestOutputHelper output) : IDisposable
{
    private const string VmName = "HyperHarbor-Test";
    private const string SwitchName = "HyperHarbor-Test-Switch";
    private static readonly Guid UserId = Guid.Parse("11111111-2222-4333-8444-555555555555");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [EnvironmentFact("HH_LIFECYCLE_LIVE")]
    public async Task CreateThenDelete_ProducesTheDocumentedVm_AndLeavesNothingBehind()
    {
        var isoFolder = Path.Combine(_root, "isos");
        Directory.CreateDirectory(isoFolder);

        // Hyper-V attaches the file without reading it; the VM is never started.
        await File.WriteAllBytesAsync(Path.Combine(isoFolder, "placeholder.iso"), new byte[2048]);

        var inventory = new VmInventory(new CimHyperVReader(NullLogger<CimHyperVReader>.Instance), new TcpRdpProbe(TimeProvider.System));
        Assert.DoesNotContain(await inventory.ListAsync(CancellationToken.None), vm => vm.Name == VmName);

        var storage = new CimHyperVStorage();
        var files = new WindowsDiskFiles();
        var options = new LifecycleOptions { VmRootFolder = Path.Combine(_root, "vms"), IsoFolder = isoFolder, HostMemoryReserveMb = 0 };
        using var jobs = new VmJobStore(new VmOperationLocks(), TimeProvider.System, NullLogger<VmJobStore>.Instance);
        var creation = new VmCreationService(
            inventory, new CimHyperVBuilder(), storage, new CimHyperVHost(), new WindowsHostCapacityReader(), files,
            new IsoLibrary(isoFolder), jobs, options, NullLogger<VmCreationService>.Instance);

        var created = await creation.StartAsync(
            UserId,
            new CreateVmRequest(VmName, "placeholder.iso", 1, 2, 1024, 2048, DynamicMemory: true, AcknowledgeWarnings: true),
            null,
            CancellationToken.None);
        await jobs.WhenFinished(created.Id);
        var job = jobs.Get(created.Id, UserId)!;
        output.WriteLine($"Create: {job.State} {job.ErrorTitle} {job.ErrorDetail}");
        Assert.Equal(VmJobState.Succeeded, job.State);
        var vmId = job.VmId!.Value;

        try
        {
            CheckSettings(vmId);

            // The developer preset on the (off) VM: static memory, nested virtualization, MAC spoofing, one processor.
            var compute = new VmComputeService(
                inventory, new CimHyperVCompute(), new Core.Power.CimHyperVPowerInvoker(), new WindowsHostCapacityReader(), new CimHyperVHost(),
                new VmOperationLocks(), jobs, options, TimeProvider.System, NullLogger<VmComputeService>.Instance);
            var update = await compute.UpdateAsync(
                vmId,
                UserId,
                new UpdateVmComputeRequest(ProcessorCount: 1, DynamicMemory: false, NestedVirtualization: true, MacAddressSpoofing: true, AcknowledgeWarnings: true),
                snapshot => throw new InvalidOperationException("An off VM needs no job."),
                null,
                CancellationToken.None);
            var applied = await new CimHyperVCompute().ReadAsync(vmId, CancellationToken.None);
            output.WriteLine($"Applied: {applied}");
            Assert.NotNull(update.Settings);
            Assert.Equal(new ComputeState(1, 1024, 1024, false, true, true, 1, applied.SwitchId, 1), applied);
            Assert.Equal(CimHyperVHost.DefaultSwitchId, applied.SwitchId, ignoreCase: true);

            await CheckSwitchChangesAsync(compute, vmId);

            // A checkpoint, so deletion has to merge it (DestroySnapshotTree) before removing the VM.
            await CreateCheckpointAsync(vmId);
            var preview = await new VmDeletionService(inventory, storage, files, jobs, NullLogger<VmDeletionService>.Instance)
                .PreviewAsync(vmId, CancellationToken.None);
            output.WriteLine($"Before delete: {preview.CheckpointCount} checkpoint(s); disks {string.Join(", ", preview.Disks)}");
            Assert.Equal(1, preview.CheckpointCount);
            Assert.Empty(preview.Blockers);
            Assert.All(preview.Disks, disk => Assert.EndsWith(".vhdx", disk, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            var deletion = new VmDeletionService(inventory, storage, files, jobs, NullLogger<VmDeletionService>.Instance);
            var deleting = await deletion.StartAsync(vmId, UserId, new VmDeleteRequest(true, true, VmName), null, CancellationToken.None);
            await jobs.WhenFinished(deleting.Id);
            var deleted = jobs.Get(deleting.Id, UserId)!;
            output.WriteLine($"Delete: {deleted.State} {deleted.ErrorDetail}");
            Assert.Equal(VmJobState.Succeeded, deleted.State);
        }

        Assert.DoesNotContain(await inventory.ListAsync(CancellationToken.None), vm => vm.Id == vmId);
        Assert.False(File.Exists(Path.Combine(_root, "vms", VmName, "Virtual Hard Disks", VmName + ".vhdx")));
        Assert.True(File.Exists(Path.Combine(isoFolder, "placeholder.iso")), "Deleting the VM's disks must keep its ISO.");
    }

    /// <summary>
    /// Moves the VM's adapter to a throwaway private switch (an existing connection is pointed elsewhere), disconnects it,
    /// connects it again through the service (a new connection is added), and puts it back on the Default Switch.
    /// </summary>
    private async Task CheckSwitchChangesAsync(VmComputeService compute, Guid vmId)
    {
        await RunPowerShellAsync($"New-VMSwitch -Name '{SwitchName}' -SwitchType Private | Out-Null");
        try
        {
            var created = (await new CimHyperVHost().ListSwitchesAsync(CancellationToken.None)).Single(item => item.Name == SwitchName);

            var moved = await ChangeSwitchAsync(compute, vmId, created.Id);
            output.WriteLine($"Moved to {SwitchName}: {moved}");
            Assert.Equal(created.Id, moved.SwitchId, ignoreCase: true);
            Assert.True(moved.MacAddressSpoofing, "Pointing the connection at another switch must keep its security settings.");

            await RunPowerShellAsync($"Disconnect-VMNetworkAdapter -VMName '{VmName}'");
            var disconnected = await new CimHyperVCompute().ReadAsync(vmId, CancellationToken.None);
            output.WriteLine($"Disconnected: {disconnected}");
            Assert.Null(disconnected.SwitchId);
            Assert.Equal(1, disconnected.AdapterCount);

            var back = await ChangeSwitchAsync(compute, vmId, CimHyperVHost.DefaultSwitchId);
            output.WriteLine($"Connected again: {back}");
            Assert.Equal(CimHyperVHost.DefaultSwitchId, back.SwitchId, ignoreCase: true);
            Assert.Equal(1, back.NetworkAdapterCount);
        }
        finally
        {
            await RunPowerShellAsync($"Get-VMNetworkAdapter -VMName '{VmName}' | Where-Object SwitchName -eq '{SwitchName}' | Connect-VMNetworkAdapter -SwitchName 'Default Switch'; Remove-VMSwitch -Name '{SwitchName}' -Force");
        }
    }

    private static async Task<ComputeState> ChangeSwitchAsync(VmComputeService compute, Guid vmId, string switchId)
    {
        var update = await compute.UpdateAsync(
            vmId, UserId, new UpdateVmComputeRequest(SwitchId: switchId), snapshot => throw new InvalidOperationException("A switch needs no job."), null, CancellationToken.None);
        Assert.NotNull(update.Settings);
        return await new CimHyperVCompute().ReadAsync(vmId, CancellationToken.None);
    }

    private static async Task RunPowerShellAsync(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
    }

    private static async Task CreateCheckpointAsync(Guid vmId)
    {
        using var session = CimSession.Create(null);
        using var service = session.QueryInstances(HyperVCim.Namespace, "WQL", "SELECT * FROM Msvm_VirtualSystemSnapshotService").Single();
        using var system = session.QueryInstances(HyperVCim.Namespace, "WQL", $"SELECT * FROM Msvm_ComputerSystem WHERE Name = '{vmId:D}'").Single();
        var parameters = new CimMethodParametersCollection
        {
            CimMethodParameter.Create("AffectedSystem", system, CimType.Reference, CimFlags.In),
            CimMethodParameter.Create("SnapshotType", (ushort)2, CimType.UInt16, CimFlags.In),
        };
        await HyperVCim.InvokeAsync(session, service, "CreateSnapshot", parameters, "CreateSnapshot", CancellationToken.None);
    }

    private void CheckSettings(Guid vmId)
    {
        using var session = CimSession.Create(null);
        using var settings = session.QueryInstances(HyperVCim.Namespace, "WQL",
            $"SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemIdentifier = '{vmId:D}' AND VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'").Single();

        Assert.Equal("Microsoft:Hyper-V:SubType:2", settings.CimInstanceProperties["VirtualSystemSubType"].Value);
        Assert.Equal(true, settings.CimInstanceProperties["SecureBootEnabled"].Value);
        Assert.Equal(CimHyperVBuilder.MicrosoftWindowsTemplateId, (string)settings.CimInstanceProperties["SecureBootTemplateId"].Value, ignoreCase: true);
        var notes = settings.CimInstanceProperties["Notes"].Value as string[] ?? [];
        output.WriteLine($"Notes: [{string.Join("|", notes.Select(note => $"\"{note}\""))}] ({notes.Length})");
        Assert.All(notes, note => Assert.True(string.IsNullOrEmpty(note), note));

        var processor = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_ProcessorSettingData", null, null).Single();
        Assert.Equal(2UL, processor.CimInstanceProperties["VirtualQuantity"].Value);

        var memory = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_MemorySettingData", null, null).Single();
        Assert.Equal(true, memory.CimInstanceProperties["DynamicMemoryEnabled"].Value);
        Assert.Equal(1024UL, memory.CimInstanceProperties["VirtualQuantity"].Value);
        Assert.Equal(2048UL, memory.CimInstanceProperties["Limit"].Value);

        var security = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_SecuritySettingData", null, null).Single();
        Assert.Equal(true, security.CimInstanceProperties["TpmEnabled"].Value);

        var bootOrder = (string[])settings.CimInstanceProperties["BootSourceOrder"].Value;
        bootOrder.ToList().ForEach(entry => output.WriteLine($"Boot: {entry}"));
        var dvd = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_ResourceAllocationSettingData", null, null)
            .Single(resource => (string)resource.CimInstanceProperties["ResourceSubType"].Value == "Microsoft:Hyper-V:Synthetic DVD Drive");
        var dvdId = ((string)dvd.CimInstanceProperties["InstanceID"].Value).Replace(@"\", @"\\", StringComparison.Ordinal);
        Assert.Contains(dvdId + @"\\B", bootOrder[0], StringComparison.OrdinalIgnoreCase);

        var adapters = session.EnumerateAssociatedInstances(HyperVCim.Namespace, settings, null, "Msvm_EthernetPortAllocationSettingData", null, null).ToList();
        output.WriteLine($"Network connections: {adapters.Count}");
    }
}
