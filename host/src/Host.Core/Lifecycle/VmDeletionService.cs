using HyperHarbor.Host.Core.Power;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>
/// Deletes virtual machines that are off, optionally with their disks and checkpoints.
/// Disk rules:
/// - The files deleted are the VM's own base disks: each attached disk, followed through checkpoint
///   (.avhdx) parents to the first disk that is not a checkpoint disk. Checkpoints are merged into those.
/// - A parent of a VM's own differencing disk is never deleted; it may be a shared template.
/// - Nothing is deleted or merged when another VM (including its checkpoints) or a differencing disk
///   in the same folder that this VM does not use depends on one of those files.
/// </summary>
public sealed class VmDeletionService
{
    /// <summary>Longest parent chain followed; longer chains are treated as broken.</summary>
    internal const int MaxChainLength = 64;

    private readonly IVmInventory _inventory;
    private readonly IHyperVStorage _storage;
    private readonly IDiskFiles _files;
    private readonly VmJobStore _jobs;
    private readonly ILogger<VmDeletionService> _logger;

    /// <summary>Forgets an unattended install, its answer file, and the stored administrator credential of a deleted VM.</summary>
    private readonly Unattend.UnattendedSetup? _unattended;
    private readonly Performance.PerformanceStore? _performance;
    private readonly Provisioning.ProvisioningStore? _provisioning;
    private readonly GameAware.GameAwareStore? _gameAware;
    private readonly Backup.BackupConfigStore? _backups;

    public VmDeletionService(
        IVmInventory inventory,
        IHyperVStorage storage,
        IDiskFiles files,
        VmJobStore jobs,
        ILogger<VmDeletionService> logger,
        Unattend.UnattendedSetup? unattended = null,
        Performance.PerformanceStore? performance = null,
        Provisioning.ProvisioningStore? provisioning = null,
        GameAware.GameAwareStore? gameAware = null,
        Backup.BackupConfigStore? backups = null)
    {
        _unattended = unattended;
        _performance = performance;
        _provisioning = provisioning;
        _gameAware = gameAware;
        _backups = backups;
        _inventory = inventory;
        _storage = storage;
        _files = files;
        _jobs = jobs;
        _logger = logger;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<VmDeletePreview> PreviewAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        // Only virtual hard disk files can ever be deleted: an attached ISO or anything else is skipped here,
        // even if storage reports it.
        var read = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        var storage = read with { Disks = read.Disks.Where(disk => DiskPaths.IsDisk(disk.Path)).ToList() };
        var parents = new ParentCache(_storage, cancellationToken);

        var own = storage.Disks.Where(disk => disk.VmId == vmId).ToList();
        var ownFiles = new HashSet<string>(DiskPaths.Comparer);
        var targets = new List<string>();
        foreach (var disk in own)
        {
            ownFiles.Add(DiskPaths.Normalize(disk.Path));
        }

        foreach (var disk in own)
        {
            // Walking checkpoint attachments too records every .avhdx of the VM's checkpoint tree as its own.
            var target = await BaseDiskAsync(disk.Path, parents, ownFiles).ConfigureAwait(false);
            if (!disk.InCheckpoint && !targets.Contains(target, DiskPaths.Comparer))
            {
                targets.Add(target);
            }
        }

        var blockers = new List<DeleteBlocker>();
        if (vm.State != VmState.Off)
        {
            blockers.Add(new DeleteBlocker(DeleteBlockerCode.NotOff, DeleteBlockerScope.Always, vm.State == VmState.Saved
                ? $"{vm.Name} has a saved state. Start it and shut it down, then delete it."
                : $"{vm.Name} is {vm.State.ToString().ToLowerInvariant()}. Shut it down or turn it off first."));
        }

        await AddSharingBlockersAsync(vmId, storage, targets, ownFiles, parents, blockers).ConfigureAwait(false);

        var existing = targets.Where(_files.Exists).ToList();

        // A VM that is not off holds its disks open, so the check is only meaningful once it is off.
        foreach (var target in existing.Where(target => vm.State == VmState.Off && !_files.CanDelete(target)))
        {
            blockers.Add(new DeleteBlocker(DeleteBlockerCode.DiskNotDeletable, DeleteBlockerScope.DeleteDisks,
                $"The host service cannot delete {target}. It is in use, or the service account lacks permission to delete it."));
        }

        return new VmDeletePreview(vmId, vm.Name, vm.State, storage.CheckpointsOf(vmId), existing, blockers);
    }

    /// <summary>Checks the request and starts the deletion job.</summary>
    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    /// <exception cref="LifecycleValidationException">The confirmation name does not match.</exception>
    /// <exception cref="LifecycleConflictException">The VM is not off, has checkpoints that were not to be deleted, or shares a disk.</exception>
    /// <exception cref="VmBusyException">Another operation holds the VM.</exception>
    public async Task<VmJobSnapshot> StartAsync(
        Guid vmId,
        Guid userId,
        VmDeleteRequest request,
        Action<VmJobSnapshot>? onFinished,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preview = await PreviewAsync(vmId, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(request.ConfirmName, preview.VmName, StringComparison.Ordinal))
        {
            throw new LifecycleValidationException(
                "Type the virtual machine's name exactly to confirm the deletion.",
                [new ValidationIssue("confirmName", $"Type \"{preview.VmName}\" to confirm.")]);
        }

        EnsureAllowed(preview, request);
        return _jobs.Start(VmJobKind.DeleteVm, vmId, userId, "Checking the virtual machine", context => RunAsync(vmId, request, context), onFinished);
    }

    private Task RunAsync(Guid vmId, VmDeleteRequest request, VmJobContext context) =>
        DeleteAsync(vmId, request, context.Report, context.Stopping);

    /// <summary>
    /// Deletes the VM as a step of a job that already holds its lock; a moved VM leaves its source host this way. The
    /// same rules apply as for <see cref="StartAsync"/>, checked again here.
    /// </summary>
    /// <param name="report">Receives each step and a percentage from 0 to 99.</param>
    /// <exception cref="LifecycleConflictException">The VM may not be deleted as requested.</exception>
    /// <exception cref="DiskDeletionException">The VM is gone, but some of its disk files are not.</exception>
    public async Task DeleteAsync(Guid vmId, VmDeleteRequest request, Action<string, int> report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The VM is locked now, but it may have changed since the request was checked.
        var preview = await PreviewAsync(vmId, cancellationToken).ConfigureAwait(false);
        EnsureAllowed(preview, request);

        if (preview.CheckpointCount > 0)
        {
            report("Merging checkpoints", 5);
            await _storage.DeleteCheckpointsAsync(vmId, percent => report("Merging checkpoints", 5 + percent * 55 / 100), cancellationToken).ConfigureAwait(false);
        }

        report("Deleting the virtual machine", 65);
        await _storage.DeleteVmAsync(vmId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Deleted virtual machine {Name} ({VmId}).", preview.VmName, vmId);
        ForgetRecords(vmId, preview.VmName);

        if (!request.DeleteDisks)
        {
            return;
        }

        report("Deleting disks", 80);
        var failed = new List<string>();
        foreach (var disk in preview.Disks)
        {
            try
            {
                _files.Delete(disk);
                _logger.LogInformation("Deleted disk {Path} of {Name}.", disk, preview.VmName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete disk {Path} of {Name}.", disk, preview.VmName);
                failed.Add(disk);
            }
        }

        if (failed.Count > 0)
        {
            throw new DiskDeletionException(
                $"The virtual machine was deleted, but these disk files could not be: {string.Join(", ", failed)}. Delete them on the host.");
        }
    }

    /// <summary>
    /// Removes what the host keeps about a deleted VM: its install record and credential, Performance mode record,
    /// provisioned accounts, game-aware policy, and backup policies. Backups already taken stay in their repositories.
    /// The VM is gone by now, so a record that cannot be removed is logged and does not fail the deletion.
    /// </summary>
    private void ForgetRecords(Guid vmId, string vmName)
    {
        var key = vmId.ToString("D");
        Forget("install record", () => _unattended?.Forget(vmId));
        Forget("Performance mode record", () => _performance?.Remove(vmId));
        Forget("provisioned accounts", () => _provisioning?.Remove(vmId));
        Forget("game-aware policy", () =>
        {
            if (_gameAware is not null && _gameAware.Load().VmPolicies.ContainsKey(key))
            {
                _gameAware.Update(settings => settings with
                {
                    VmPolicies = settings.VmPolicies.Where(policy => policy.Key != key).ToDictionary(policy => policy.Key, policy => policy.Value),
                });
            }
        });
        Forget("backup policies", () =>
        {
            if (_backups is not null && _backups.Load().Policies.Any(policy => IsFor(policy, vmId)))
            {
                _backups.Update(config => config with { Policies = config.Policies.Where(policy => !IsFor(policy, vmId)).ToList() });
            }
        });

        void Forget(string what, Action remove)
        {
            try
            {
                remove();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                _logger.LogWarning(ex, "Could not remove the {What} of deleted virtual machine {Name} ({VmId}).", what, vmName, vmId);
            }
        }

        static bool IsFor(Backup.BackupPolicy policy, Guid vmId) => Guid.TryParse(policy.VmId, out var id) && id == vmId;
    }

    private static void EnsureAllowed(VmDeletePreview preview, VmDeleteRequest request)
    {
        if (preview.CheckpointCount > 0 && !request.DeleteCheckpoints)
        {
            throw new LifecycleConflictException(
                $"{preview.VmName} has {preview.CheckpointCount} checkpoint(s). Choose to delete them; they are merged into its disks first.");
        }

        var blocking = preview.Blockers.FirstOrDefault(blocker => blocker.Scope switch
        {
            DeleteBlockerScope.Always => true,
            DeleteBlockerScope.DeleteDisks => request.DeleteDisks,
            DeleteBlockerScope.DeleteDisksOrCheckpoints => request.DeleteDisks || (request.DeleteCheckpoints && preview.CheckpointCount > 0),
            _ => true,
        });
        if (blocking is not null)
        {
            throw new LifecycleConflictException(blocking.Message);
        }
    }

    /// <summary>Follows checkpoint disks up to the first ordinary disk, which checkpoint deletion merges into.</summary>
    private static async Task<string> BaseDiskAsync(string attached, ParentCache parents, HashSet<string> ownFiles)
    {
        var current = DiskPaths.Normalize(attached);
        for (var depth = 0; depth < MaxChainLength && DiskPaths.IsCheckpointDisk(current); depth++)
        {
            if (await parents.GetAsync(current).ConfigureAwait(false) is not { } parent)
            {
                break;
            }

            current = parent;
            ownFiles.Add(current);
        }

        return current;
    }

    private async Task AddSharingBlockersAsync(
        Guid vmId,
        StorageSnapshot storage,
        List<string> targets,
        HashSet<string> ownFiles,
        ParentCache parents,
        List<DeleteBlocker> blockers)
    {
        if (targets.Count == 0)
        {
            return;
        }

        var targetSet = new HashSet<string>(targets, DiskPaths.Comparer);
        var reported = new HashSet<string>(DiskPaths.Comparer);

        // Other VMs, including disks referenced only by their checkpoints.
        var users = new Dictionary<string, SortedSet<string>>(DiskPaths.Comparer);
        foreach (var disk in storage.Disks.Where(disk => disk.VmId != vmId))
        {
            foreach (var file in await ChainAsync(disk.Path, parents).ConfigureAwait(false))
            {
                if (targetSet.Contains(file))
                {
                    if (!users.TryGetValue(file, out var names))
                    {
                        users[file] = names = new SortedSet<string>(StringComparer.CurrentCultureIgnoreCase);
                    }

                    names.Add(disk.VmName);
                }
            }
        }

        foreach (var (file, names) in users)
        {
            reported.Add(file);
            blockers.Add(new DeleteBlocker(DeleteBlockerCode.SharedDisk, DeleteBlockerScope.DeleteDisksOrCheckpoints,
                $"{file} is also used by {string.Join(", ", names)}."));
        }

        // Differencing disks next to the VM's disks that no VM uses, but that depend on them.
        var otherFiles = storage.Disks.Where(disk => disk.VmId != vmId).Select(disk => DiskPaths.Normalize(disk.Path)).ToHashSet(DiskPaths.Comparer);
        foreach (var directory in targets.Select(Path.GetDirectoryName).OfType<string>().Distinct(DiskPaths.Comparer))
        {
            foreach (var candidate in _files.ListDiskFiles(directory).Select(DiskPaths.Normalize))
            {
                if (ownFiles.Contains(candidate) || otherFiles.Contains(candidate) || targetSet.Contains(candidate))
                {
                    continue;
                }

                foreach (var file in (await ChainAsync(candidate, parents).ConfigureAwait(false)).Skip(1))
                {
                    if (targetSet.Contains(file) && reported.Add(file + "|" + candidate))
                    {
                        blockers.Add(new DeleteBlocker(DeleteBlockerCode.SharedDisk, DeleteBlockerScope.DeleteDisksOrCheckpoints,
                            $"{file} is the parent of {candidate}, which this VM does not use."));
                    }
                }
            }
        }
    }

    /// <summary>The file and all its ancestors, normalized, stopping at a missing parent, a cycle, or the length limit.</summary>
    private static async Task<List<string>> ChainAsync(string path, ParentCache parents)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>(DiskPaths.Comparer);
        string? current = DiskPaths.Normalize(path);
        while (current is not null && chain.Count < MaxChainLength && seen.Add(current))
        {
            chain.Add(current);
            current = await parents.GetAsync(current).ConfigureAwait(false);
        }

        return chain;
    }

    /// <summary>Each disk's parent is read from Hyper-V once per preview.</summary>
    private sealed class ParentCache(IHyperVStorage storage, CancellationToken cancellationToken)
    {
        private readonly Dictionary<string, string?> _parents = new(DiskPaths.Comparer);

        public async Task<string?> GetAsync(string path)
        {
            if (!_parents.TryGetValue(path, out var parent))
            {
                var raw = await storage.GetParentPathAsync(path, cancellationToken).ConfigureAwait(false);
                parent = raw is null ? null : DiskPaths.Normalize(raw);
                _parents[path] = parent;
            }

            return parent;
        }
    }
}
