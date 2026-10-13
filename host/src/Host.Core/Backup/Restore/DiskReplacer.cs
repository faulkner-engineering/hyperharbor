using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Backup.Restore;

public sealed record ReplaceDiskRequest(Guid VmId, string TypedName, BackupTarget Target, string SnapshotId);

/// <summary>
/// Puts a snapshot's disks over an existing VM's disks. The VM must be off, have no checkpoints, and the caller must type its
/// exact name. The old disks are kept as .pre-restore files until every new disk is in place, then removed; any failure
/// puts them back.
/// </summary>
public sealed class DiskReplacer(
    IBackupRepositories repositories,
    ISnapshotRestorer restorer,
    IVmInventory inventory,
    IHyperVDefinitionReader definitions,
    IHyperVCheckpoints checkpoints,
    ILogger<DiskReplacer> logger)
{
    public const string PreRestoreSuffix = ".pre-restore";

    public async Task ReplaceAsync(ReplaceDiskRequest request, Action<string, int> progress, CancellationToken cancellationToken)
    {
        var vm = await inventory.GetAsync(request.VmId, cancellationToken).ConfigureAwait(false)
            ?? throw new RestoreException("The virtual machine no longer exists.");
        if (!string.Equals(request.TypedName, vm.Name, StringComparison.Ordinal))
        {
            throw new RestoreException("The typed name does not match the virtual machine's name.");
        }

        if (vm.State != VmState.Off)
        {
            throw new RestoreException("Turn the virtual machine off before replacing its disk.");
        }

        if ((await checkpoints.ListAsync(request.VmId, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw new RestoreException("The virtual machine has checkpoints. Remove them before replacing its disk.");
        }

        var definition = await definitions.ReadAsync(request.VmId, cancellationToken).ConfigureAwait(false);
        if (definition.Disks.Count == 0)
        {
            throw new RestoreException("The virtual machine has no disk to replace.");
        }

        var scratch = Path.Combine(Path.GetDirectoryName(definition.Disks[0].Path)!, $".hh-restore-{Guid.NewGuid():N}");
        var moved = new List<(string Original, string Kept)>();
        var placed = new List<string>();
        try
        {
            progress("Opening the backup repository", 3);
            var repository = await repositories.OpenAsync(request.Target, RepositoryRole.Vm, cancellationToken).ConfigureAwait(false);
            progress("Restoring the files", 8);
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

            if (manifest.Disks.Count != definition.Disks.Count)
            {
                throw new RestoreException(
                    $"The backup has {manifest.Disks.Count} disk(s) but the virtual machine has {definition.Disks.Count}. Restore it as a new VM instead.");
            }

            progress("Swapping the disks", 80);
            for (var i = 0; i < definition.Disks.Count; i++)
            {
                var current = definition.Disks[i].Path;
                var restored = Path.Combine(scratch, manifest.Disks[i].File);
                if (!File.Exists(restored))
                {
                    throw new RestoreException($"The backup is missing the disk {manifest.Disks[i].File}.");
                }

                var kept = current + PreRestoreSuffix;
                File.Move(current, kept, overwrite: true);
                moved.Add((current, kept));
                File.Move(restored, current);
                placed.Add(current);
            }

            foreach (var (_, kept) in moved)
            {
                File.Delete(kept);
            }

            progress("Done", 99);
        }
        catch
        {
            // Put the old disks back, newest swap first.
            for (var i = moved.Count - 1; i >= 0; i--)
            {
                try
                {
                    var (original, kept) = moved[i];
                    if (placed.Contains(original))
                    {
                        File.Delete(original);
                    }

                    File.Move(kept, original, overwrite: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "The original disk {Disk} could not be put back after a failed restore.", moved[i].Original);
                }
            }

            throw;
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove {Folder} after replacing a disk.", scratch);
            }
        }
    }
}
