using System.Globalization;
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
        long? uploaded = null;
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
                var earlier = history.LastSuccess(BackupCoordinator.VmItem(request.VmId), request.Target.Id) is not null;
                var splitterChanged = earlier
                    && await kopia.ReadSplitterAsync(repository.Repository, repository.Passphrase, stagingFolder, cancellationToken).ConfigureAwait(false)
                        != KopiaCli.DiskSplitter;
                await kopia.SetPolicyAsync(
                    repository.Repository, repository.Passphrase, stagingFolder, request.Policy.Retention, cancellationToken, KopiaCli.DiskSplitter).ConfigureAwait(false);
                await kopia.SetBandwidthAsync(repository.Repository, repository.Passphrase, request.Policy.BandwidthKbps, cancellationToken).ConfigureAwait(false);

                progress("Uploading", 35);
                var (snapshot, measured) = await kopia.CreateMeasuredSnapshotAsync(
                    repository.Repository, repository.Passphrase, stagingFolder,
                    $"hyperharbor vm={request.VmId:D} name={definition.Name} run={runId:N}", cancellationToken).ConfigureAwait(false);
                snapshotId = snapshot.Id;
                bytes = snapshot.TotalSize;
                uploaded = measured;
                if (UploadWarning(bytes, uploaded, earlier, splitterChanged) is { } uploadWarning)
                {
                    warnings.Add(uploadWarning);
                }

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
                started, time.GetUtcNow(), outcome, reason, snapshotId, bytes, warnings, uploaded);
            history.Add(record);
            progress("Done", 99);
            return record;
        }
    }

    /// <summary>
    /// The warning for a run that uploaded nearly a full copy of the disks, or null. The first backup of a VM to a target
    /// is a full copy anyway, and a small VM is not worth a warning.
    /// </summary>
    internal static string? UploadWarning(long totalBytes, long? uploadedBytes, bool earlierBackup, bool splitterChanged)
    {
        if (!earlierBackup || uploadedBytes is not { } uploaded || totalBytes < FullUploadMinimumBytes || uploaded < totalBytes * FullUploadRatio)
        {
            return null;
        }

        var sizes = $"It uploaded {Gigabytes(uploaded)} GB of {Gigabytes(totalBytes)} GB.";
        return splitterChanged
            ? $"This backup stored the disks in a new layout that changes far less on later backups, so it uploaded them in full once. {sizes}"
            : $"This backup uploaded nearly a full copy of the disks. {sizes} This happens when the guest rewrote most of its disks, "
                + "for example when it turned on or changed BitLocker encryption. If it happens every time, the guest's disks are "
                + "probably encrypted: an encrypted disk does not compress, so every changed part uploads in full.";
    }

    /// <summary>A run that uploads at least this share of the disks counts as a full copy.</summary>
    internal const double FullUploadRatio = 0.8;

    internal const long FullUploadMinimumBytes = 1L << 30;

    private static string Gigabytes(long bytes) => (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture);

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
