using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup;

public sealed record VmBackupRequest(Guid VmId, BackupTarget Target, BackupPolicy Policy, BackupTrigger Trigger);

/// <summary>
/// Backs up one VM to one target. A running VM is read from a production checkpoint's frozen parent disk (a standard
/// checkpoint when the guest cannot do a production one), and the checkpoint is removed again whatever happens. The disks
/// reach Kopia as hard links in a staging folder next to the first disk, with manifest.json beside them.
/// </summary>
public sealed class VmBackupService(
    IVmInventory inventory,
    IHyperVDefinitionReader definitions,
    IHyperVCheckpoints checkpoints,
    IHyperVStorage storage,
    IBackupStaging staging,
    IBackupRepositories repositories,
    KopiaCli kopia,
    BackupHistoryStore history,
    PerformanceStore performance,
    Func<Guid, bool> isProvisioned,
    TimeProvider time,
    ILogger<VmBackupService> logger,
    string hostVersion)
{
    public const string StagingFolderName = ".hh-backup";

    /// <summary>Always returns a record (and writes it to the history); never throws for a failed backup.</summary>
    public async Task<BackupRunRecord> RunAsync(VmBackupRequest request, Action<string, int> progress, CancellationToken cancellationToken)
    {
        var runId = Guid.NewGuid();
        var started = time.GetUtcNow();
        var warnings = new List<string>();
        Vm? vm = null;
        string? snapshotId = null;
        long bytes = 0;
        var outcome = BackupOutcome.Failed;
        string? reason = null;
        try
        {
            vm = await inventory.GetAsync(request.VmId, cancellationToken).ConfigureAwait(false)
                ?? throw new BackupException("The virtual machine no longer exists.");

            progress("Removing leftovers of an earlier backup", 2);
            await RemoveStaleCheckpointsAsync(request.VmId, warnings).ConfigureAwait(false);

            progress("Checking the virtual machine", 5);
            var before = await definitions.ReadAsync(request.VmId, cancellationToken).ConfigureAwait(false);
            var existing = await checkpoints.ListAsync(request.VmId, cancellationToken).ConfigureAwait(false);
            var plan = BackupConsistencyPlanner.Plan(new ConsistencyInputs(
                vm.State, before.HasGpuPartition, request.Policy.GpuLiveCopy,
                existing.Count(c => !c.IsHyperHarborBackup), before.Disks.Any(d => d.IsDifferencing)));
            if (plan.Action == ConsistencyAction.Skip)
            {
                outcome = BackupOutcome.Skipped;
                reason = plan.Reason.ToString();
                warnings.Add(plan.Warning!);
                return Finish();
            }

            if (plan.Warning is not null)
            {
                warnings.Add(plan.Warning);
            }

            progress("Opening the backup repository", 8);
            var repository = await repositories.OpenAsync(request.Target, RepositoryRole.Vm, cancellationToken).ConfigureAwait(false);

            var checkpointAttempted = false;
            var knownCheckpoints = existing.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
            string? stagingFolder = null;
            var mode = plan.Action switch
            {
                ConsistencyAction.DirectCopy => ConsistencyMode.Off,
                ConsistencyAction.LiveCopy => ConsistencyMode.Live,
                _ => ConsistencyMode.Production,
            };
            try
            {
                if (plan.Action == ConsistencyAction.Checkpoint)
                {
                    progress("Taking a checkpoint", 12);
                    checkpointAttempted = true;
                    var name = $"{CheckpointInfo.BackupPrefix}{runId:N}";
                    try
                    {
                        await checkpoints.CreateAsync(request.VmId, name, CheckpointKind.Production, cancellationToken).ConfigureAwait(false);
                    }
                    catch (CheckpointException ex)
                    {
                        logger.LogInformation(ex, "Production checkpoint failed for {Vm}; using a standard checkpoint.", request.VmId);
                        await checkpoints.CreateAsync(request.VmId, name, CheckpointKind.Standard, cancellationToken).ConfigureAwait(false);
                        mode = ConsistencyMode.Standard;
                        warnings.Add("The guest could not take an application-consistent checkpoint, so this backup is crash consistent.");
                    }
                }

                progress("Reading the virtual machine's settings", 20);
                var definition = await definitions.ReadAsync(request.VmId, cancellationToken).ConfigureAwait(false);
                var frozen = new List<string>();
                foreach (var disk in definition.Disks)
                {
                    frozen.Add(disk.IsDifferencing
                        ? await storage.GetParentPathAsync(disk.Path, cancellationToken).ConfigureAwait(false)
                            ?? throw new BackupException($"The parent of {disk.Path} could not be found.")
                        : disk.Path);
                }

                if (frozen.Count == 0)
                {
                    throw new BackupException("The virtual machine has no virtual hard disks to back up.");
                }

                progress("Preparing the files", 25);
                stagingFolder = Path.Combine(Path.GetDirectoryName(frozen[0])!, StagingFolderName, request.VmId.ToString("N"));
                staging.Prepare(stagingFolder);
                var names = new List<string>();
                var sizes = new List<long>();
                for (var i = 0; i < frozen.Count; i++)
                {
                    var file = $"disk-{i}{Path.GetExtension(frozen[i]).ToLowerInvariant()}";
                    staging.Link(frozen[i], Path.Combine(stagingFolder, file));
                    names.Add(file);
                    sizes.Add(staging.Length(frozen[i]));
                }

                var manifest = ManifestBuilder.Build(
                    definition, names, sizes, performance.Find(request.VmId), mode, warnings,
                    vm.GuestOs is { Family: not GuestOsFamily.Unknown } os ? os.Name ?? os.Family.ToString() : null,
                    isProvisioned(request.VmId), hostVersion, started);
                staging.WriteFile(Path.Combine(stagingFolder, BackupManifest.FileName), manifest.ToJson());

                progress("Setting retention", 30);
                await kopia.SetPolicyAsync(repository.Repository, repository.Passphrase, stagingFolder, request.Policy.Retention, cancellationToken).ConfigureAwait(false);
                await kopia.SetBandwidthAsync(repository.Repository, repository.Passphrase, request.Policy.BandwidthKbps, cancellationToken).ConfigureAwait(false);

                progress("Uploading", 35);
                var snapshot = await kopia.CreateSnapshotAsync(
                    repository.Repository, repository.Passphrase, stagingFolder,
                    $"hyperharbor vm={request.VmId:D} name={definition.Name} run={runId:N}", cancellationToken).ConfigureAwait(false);
                snapshotId = snapshot.Id;
                bytes = snapshot.TotalSize;
                outcome = BackupOutcome.Succeeded;
            }
            finally
            {
                // Cleanup never takes the caller's cancellation: a cancelled backup must still remove its checkpoint.
                progress("Cleaning up", 92);
                if (stagingFolder is not null)
                {
                    try
                    {
                        staging.Remove(stagingFolder);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        logger.LogWarning(ex, "The backup staging folder {Folder} could not be removed.", stagingFolder);
                        warnings.Add("The temporary backup folder could not be removed; the next backup removes it.");
                    }
                }

                if (checkpointAttempted)
                {
                    // Any checkpoint that was not there before this run is ours, however the run ended and even when
                    // creating it reported an error after Hyper-V had made it.
                    if (!await RemoveNewCheckpointsAsync(request.VmId, knownCheckpoints, warnings).ConfigureAwait(false) && outcome == BackupOutcome.Succeeded)
                    {
                        outcome = BackupOutcome.Partial;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "Cancelled";
            warnings.Add("The backup was cancelled.");
        }
        catch (Exception ex) when (ex is BackupException or KopiaException or KopiaUnavailableException or CheckpointException
            or Power.VmNotFoundException or Power.HyperVOperationException or HyperHarbor.Host.Core.HyperV.HyperVJobFailedException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Backup of {Vm} to {Target} failed.", request.VmId, request.Target.Id);
            reason = ex.Message;
        }

        return Finish();

        BackupRunRecord Finish()
        {
            var record = new BackupRunRecord(
                runId, $"vm:{request.VmId:D}", request.VmId, vm?.Name, request.Target.Id, request.Trigger,
                started, time.GetUtcNow(), outcome, reason, snapshotId, bytes, warnings);
            history.Add(record);
            progress("Done", 99);
            return record;
        }
    }

    /// <returns>False when a checkpoint could not be removed.</returns>
    private async Task<bool> RemoveNewCheckpointsAsync(Guid vmId, HashSet<string> known, List<string> warnings)
    {
        var clean = true;
        IReadOnlyList<CheckpointInfo> now;
        try
        {
            now = await checkpoints.ListAsync(vmId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HyperHarbor.Host.Core.HyperV.HyperVUnavailableException or Power.HyperVOperationException)
        {
            logger.LogError(ex, "The checkpoints of {Vm} could not be listed after a backup.", vmId);
            warnings.Add("The checkpoints could not be checked after the backup. Look for one named \"" + CheckpointInfo.BackupPrefix + "...\" in Hyper-V Manager.");
            return false;
        }

        foreach (var checkpoint in now.Where(c => !known.Contains(c.Id)))
        {
            try
            {
                await checkpoints.RemoveAsync(vmId, checkpoint.Id, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The backup checkpoint {Checkpoint} of {Vm} could not be removed.", checkpoint.Name, vmId);
                warnings.Add($"The checkpoint \"{checkpoint.Name}\" could not be removed. Remove it in Hyper-V Manager; the next backup also tries.");
                clean = false;
            }
        }

        return clean;
    }

    /// <summary>Removes checkpoints a failed earlier run of this service left behind (found by name).</summary>
    private async Task RemoveStaleCheckpointsAsync(Guid vmId, List<string> warnings)
    {
        foreach (var stale in (await checkpoints.ListAsync(vmId, CancellationToken.None).ConfigureAwait(false)).Where(c => c.IsHyperHarborBackup))
        {
            try
            {
                await checkpoints.RemoveAsync(vmId, stale.Id, CancellationToken.None).ConfigureAwait(false);
                warnings.Add($"Removed a checkpoint left by an earlier backup (\"{stale.Name}\").");
            }
            catch (Exception ex) when (ex is Power.HyperVOperationException or HyperHarbor.Host.Core.HyperV.HyperVJobFailedException)
            {
                throw new BackupException($"A checkpoint left by an earlier backup (\"{stale.Name}\") could not be removed: {ex.Message}", ex);
            }
        }
    }
}
