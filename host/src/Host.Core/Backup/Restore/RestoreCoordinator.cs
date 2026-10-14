using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup.Restore;

/// <summary>Restores a snapshot's files into a folder.</summary>
public interface ISnapshotRestorer
{
    Task RestoreAsync(OpenRepository repository, string snapshotId, string destination, CancellationToken cancellationToken);
}

public sealed class KopiaSnapshotRestorer(KopiaCli kopia) : ISnapshotRestorer
{
    public Task RestoreAsync(OpenRepository repository, string snapshotId, string destination, CancellationToken cancellationToken) =>
        kopia.RestoreAsync(repository.Repository, repository.Passphrase, snapshotId, destination, cancellationToken);
}

/// <summary>Waits until a started guest reports its operating system through data exchange.</summary>
public interface IGuestHeartbeat
{
    Task<bool> WaitAsync(Guid vmId, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed class InventoryGuestHeartbeat(IVmInventory inventory, TimeProvider time) : IGuestHeartbeat
{
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<bool> WaitAsync(Guid vmId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            var vm = await inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false);
            if (vm is { State: VmState.Running, GuestOs: not null })
            {
                return true;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return false;
            }

            await Task.Delay(Poll, time, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>Creates the HyperHarbor account in a restored guest, as the Provision VM flow does.</summary>
public interface IRestoreProvisioner
{
    Task ProvisionAsync(Guid vmId, ProvisionVmRequest administrator, CancellationToken cancellationToken);
}

public enum RestoreOutcome
{
    Succeeded,
    SucceededWithWarnings,

    /// <summary>The VM was restored and started but never reported a heartbeat; it is kept, and the console shows what it waits for.</summary>
    NeedsConsole,
}

public sealed record RestoreVmRequest(
    BackupTarget Target,
    string SnapshotId,
    string DestinationRoot,
    string? Name = null,
    bool Start = false,
    ProvisionVmRequest? Administrator = null,
    bool Isolated = false);

public sealed record RestoreVmResult(
    RestoreOutcome Outcome,
    Guid VmId,
    string VmName,
    IReadOnlyList<string> Warnings,
    bool NeedsDriverResync,
    bool BitLockerRecoveryMayBeNeeded,
    bool GuestReported = false);

/// <summary>
/// Restores one VM from a snapshot: files, then the VM defined from the manifest on a new ID with a fresh virtual TPM key
/// protector, then (optionally) start, wait for the guest, and provision. A failure before the VM starts removes
/// everything this restore created; nothing that existed before is touched.
/// </summary>
public sealed class RestoreCoordinator(
    IBackupRepositories repositories,
    ISnapshotRestorer restorer,
    IVmInventory inventory,
    IHyperVHost host,
    IHyperVImporter importer,
    IHyperVCompute compute,
    IHyperVPerformance performance,
    IHyperVStorage storage,
    IHyperVPowerInvoker power,
    IGuestHeartbeat heartbeat,
    IRestoreProvisioner provisioner,
    PerformanceStore performanceStore,
    TimeProvider time,
    ILogger<RestoreCoordinator> logger)
{
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(5);
    public const string DiskFolderName = "Virtual Hard Disks";

    /// <exception cref="RestoreException">The restore could not be completed and was rolled back.</exception>
    public async Task<RestoreVmResult> RestoreAsync(RestoreVmRequest request, Action<string, int> progress, CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        var scratch = Path.Combine(request.DestinationRoot, $".hh-restore-{Guid.NewGuid():N}");
        string? vmFolder = null;
        Guid? created = null;
        try
        {
            progress("Opening the backup repository", 2);
            var repository = await repositories.OpenAsync(request.Target, RepositoryRole.Vm, cancellationToken).ConfigureAwait(false);

            progress("Restoring the files", 5);
            Directory.CreateDirectory(scratch);
            await restorer.RestoreAsync(repository, request.SnapshotId, scratch, cancellationToken).ConfigureAwait(false);

            var manifestPath = Path.Combine(scratch, BackupManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                throw new RestoreException("The snapshot has no manifest.json, so it is not a HyperHarbor VM backup.");
            }

            BackupManifest manifest;
            try
            {
                manifest = BackupManifest.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
            }
            catch (InvalidDataException ex)
            {
                throw new RestoreException(ex.Message, ex);
            }

            if (manifest.Vm.Generation != 2)
            {
                throw new RestoreException($"\"{manifest.Vm.Name}\" is a Generation {manifest.Vm.Generation} VM. Restoring Generation 1 VMs is not supported yet.");
            }

            if (manifest.Disks.Count == 0)
            {
                throw new RestoreException("The backup holds no disks.");
            }

            progress("Choosing a name", 60);
            var existing = (await inventory.ListAsync(cancellationToken).ConfigureAwait(false)).Select(v => v.Name);
            var folders = Directory.Exists(request.DestinationRoot)
                ? Directory.EnumerateDirectories(request.DestinationRoot).Select(Path.GetFileName).OfType<string>()
                : [];
            var name = RestoreNaming.UniqueName(request.Name ?? manifest.Vm.Name, existing.Concat(folders));
            if (!string.Equals(name, request.Name ?? manifest.Vm.Name, StringComparison.Ordinal))
            {
                warnings.Add($"A VM or folder named \"{request.Name ?? manifest.Vm.Name}\" already exists, so this one is named \"{name}\".");
            }

            progress("Placing the disks", 65);
            vmFolder = Path.Combine(request.DestinationRoot, name);
            var diskFolder = Path.Combine(vmFolder, DiskFolderName);
            Directory.CreateDirectory(diskFolder);
            var diskPaths = new List<string>();
            foreach (var disk in manifest.Disks)
            {
                var source = Path.Combine(scratch, disk.File);
                if (!File.Exists(source))
                {
                    throw new RestoreException($"The backup is missing the disk {disk.File}.");
                }

                var target = Path.Combine(diskFolder, Path.GetFileName(disk.File));
                File.Move(source, target);
                diskPaths.Add(target);
            }

            progress("Mapping the network", 72);
            var switches = await host.ListSwitchesAsync(cancellationToken).ConfigureAwait(false);
            // An isolated restore (the restore test) must never reach the network, so no adapter is connected.
            var network = request.Isolated
                ? new SwitchMapper.Mapping(manifest.Network.Select(_ => new ImportAdapter(null)).ToList(), [])
                : SwitchMapper.Map(manifest.Network, switches);
            warnings.AddRange(network.Warnings);

            progress("Defining the virtual machine", 75);
            var plan = ImportPlanBuilder.Build(manifest, name, vmFolder, diskPaths, network.Adapters);
            var vmId = await importer.CreateAsync(plan, cancellationToken).ConfigureAwait(false);
            created = vmId;

            if (manifest.Compute.NestedVirtualization || manifest.Compute.MacAddressSpoofing)
            {
                progress("Applying compute settings", 82);
                var desired = new ComputeState(
                    plan.ProcessorCount, plan.StartupMemoryMb, plan.MaximumMemoryMb, plan.DynamicMemory,
                    manifest.Compute.NestedVirtualization, manifest.Compute.MacAddressSpoofing, plan.Adapters.Count);
                await compute.ApplyAsync(
                    vmId, new ComputeChange(NestedVirtualization: manifest.Compute.NestedVirtualization, MacAddressSpoofing: manifest.Compute.MacAddressSpoofing),
                    desired, cancellationToken).ConfigureAwait(false);
            }

            var bitLocker = false;
            if (manifest.Tpm)
            {
                progress("Creating a new virtual TPM key", 86);
                await importer.EnableTpmAsync(vmId, cancellationToken).ConfigureAwait(false);
                bitLocker = true;
                warnings.Add("The virtual TPM has a new key. If the guest uses BitLocker, Windows asks for the recovery key at its first start.");
            }

            var needsResync = false;
            if (manifest.Gpu is { } gpu)
            {
                progress("Restoring the GPU partition", 90);
                needsResync = true;
                try
                {
                    await performance.ApplyAsync(
                        vmId, new PerformancePlan(new GpuPartitionPlan(null, gpu.Vram, gpu.Encode, gpu.Decode, gpu.Compute), gpu.LowMmioGapMb, gpu.HighMmioGapMb),
                        cancellationToken).ConfigureAwait(false);
                    if (manifest.Performance is { Applied: true, Settings: { } settings })
                    {
                        performanceStore.Save(new PerformanceRecord(vmId, settings, time.GetUtcNow()));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "The GPU partition of restored VM {Name} could not be applied.", name);
                    warnings.Add("The GPU partition could not be restored on this host. Apply Performance mode again once the VM is running.");
                }

                warnings.Add("Open Performance mode and sync the GPU driver: the restored guest holds the driver of the PC it was backed up from.");
            }

            Cleanup(scratch);
            vmFolder = null;

            var outcome = RestoreOutcome.Succeeded;
            var guestReported = false;
            if (request.Start)
            {
                progress("Starting the virtual machine", 93);
                var started = true;
                try
                {
                    await power.InvokeAsync(vmId, VmAction.Start, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is VmActionNotAllowedException or HyperVOperationException)
                {
                    // The restored VM is complete; a start that fails (for example for lack of memory) must not undo it.
                    started = false;
                    logger.LogWarning(ex, "Restored VM {Name} could not be started.", name);
                    warnings.Add($"The VM was restored but could not be started: {ex.Message}");
                }

                if (started && await heartbeat.WaitAsync(vmId, HeartbeatTimeout, cancellationToken).ConfigureAwait(false))
                {
                    guestReported = true;
                    if (request.Administrator is { } administrator && manifest.Guest.Provisioned)
                    {
                        progress("Setting up your account", 98);
                        try
                        {
                            await provisioner.ProvisionAsync(vmId, administrator, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            logger.LogWarning(ex, "Provisioning restored VM {Name} failed.", name);
                            warnings.Add("Your HyperHarbor account could not be created in the guest. Use Provision VM once it is ready.");
                        }
                    }
                }
                else if (started)
                {
                    outcome = RestoreOutcome.NeedsConsole;
                    warnings.Add("The guest did not report in within 5 minutes. The VM was kept: open its console to see what it waits for.");
                }
            }

            if (outcome == RestoreOutcome.Succeeded && warnings.Count > 0)
            {
                outcome = RestoreOutcome.SucceededWithWarnings;
            }

            return new RestoreVmResult(outcome, vmId, name, warnings, needsResync, bitLocker, guestReported);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RestoreException)
        {
            await RollBackAsync(created, vmFolder).ConfigureAwait(false);
            Cleanup(scratch);
            throw ex is BackupException or KopiaException ? new RestoreException(ex.Message, ex) : ex;
        }
        catch
        {
            await RollBackAsync(created, vmFolder).ConfigureAwait(false);
            Cleanup(scratch);
            throw;
        }
    }

    private async Task RollBackAsync(Guid? vmId, string? vmFolder)
    {
        try
        {
            if (vmId is { } id)
            {
                await storage.DeleteVmAsync(id, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "The VM created by a failed restore could not be removed.");
        }

        Cleanup(vmFolder);
    }

    private void Cleanup(string? folder)
    {
        try
        {
            if (folder is not null && Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {Folder} after a restore.", folder);
        }
    }
}
