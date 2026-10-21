using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Host.Core.Power;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>
/// Reads a VM on the sending host and says whether it can be transferred. Only an Off VM without checkpoints,
/// whose disks are its own and are not differencing disks, can go: the target imports the configuration and
/// the disks exactly as they are.
/// </summary>
public sealed class TransferPreviewService
{
    private readonly IVmInventory _inventory;
    private readonly IHyperVDefinitionReader _definitions;
    private readonly IHyperVStorage _storage;
    private readonly IDiskFiles _files;
    private readonly IHyperVVersions _versions;
    private readonly UnattendedInstallStore? _installs;
    private readonly BackupConfigStore? _backups;
    private readonly IKeyProtectorAccess? _keys;

    public TransferPreviewService(
        IVmInventory inventory,
        IHyperVDefinitionReader definitions,
        IHyperVStorage storage,
        IDiskFiles files,
        IHyperVVersions versions,
        UnattendedInstallStore? installs = null,
        BackupConfigStore? backups = null,
        IKeyProtectorAccess? keys = null)
    {
        _inventory = inventory;
        _definitions = definitions;
        _storage = storage;
        _files = files;
        _versions = versions;
        _installs = installs;
        _backups = backups;
        _keys = keys;
    }

    /// <exception cref="VmNotFoundException">No VM has this ID.</exception>
    public async Task<TransferPreview> PreviewAsync(Guid vmId, CancellationToken cancellationToken)
    {
        var vm = await _inventory.GetAsync(vmId, cancellationToken).ConfigureAwait(false) ?? throw new VmNotFoundException(vmId);
        var definition = await _definitions.ReadAsync(vmId, cancellationToken).ConfigureAwait(false);
        var version = await _versions.VmVersionAsync(vmId, cancellationToken).ConfigureAwait(false);
        var storage = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        var checkpoints = storage.CheckpointsOf(vmId);

        var blockers = new List<TransferBlocker>();
        if (vm.State == VmState.Saved)
        {
            blockers.Add(new(TransferBlockerCode.SavedState, $"{vm.Name} has a saved state. Start it and shut it down, then transfer it."));
        }
        else if (vm.State != VmState.Off)
        {
            blockers.Add(new(TransferBlockerCode.NotOff, $"{vm.Name} is {vm.State.ToString().ToLowerInvariant()}. Shut it down first."));
        }

        if (checkpoints > 0)
        {
            blockers.Add(new(TransferBlockerCode.Checkpoints,
                $"{vm.Name} has {checkpoints} checkpoint(s). Delete them first; they are merged into its disks."));
        }

        if (_installs?.Find(vmId) is { IsActive: true })
        {
            blockers.Add(new(TransferBlockerCode.InstallInProgress, $"Windows or Linux setup is still running on {vm.Name}. Wait until it is ready."));
        }

        var disks = new List<TransferDisk>();
        foreach (var disk in definition.Disks)
        {
            if (_files.SizeBytes(disk.Path) is not { } size)
            {
                blockers.Add(new(TransferBlockerCode.DiskMissing, $"The disk {disk.Path} is missing or cannot be read."));
                continue;
            }

            disks.Add(new TransferDisk(disk.Path, size, disk.ControllerType, disk.ControllerNumber, disk.Lun));

            // With checkpoints the current disk is a checkpoint's differencing disk; merging clears that.
            if (!(checkpoints > 0 && disk.IsDifferencing)
                && await _storage.GetParentPathAsync(disk.Path, cancellationToken).ConfigureAwait(false) is { } parent)
            {
                blockers.Add(new(TransferBlockerCode.DifferencingDisk,
                    $"The disk {disk.Path} is a differencing disk of {parent}. Only disks that stand alone can be transferred."));
            }

            var others = storage.Disks
                .Where(other => other.VmId != vmId && DiskPaths.Comparer.Equals(DiskPaths.Normalize(other.Path), DiskPaths.Normalize(disk.Path)))
                .Select(other => other.VmName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (others.Count > 0)
            {
                blockers.Add(new(TransferBlockerCode.SharedDisk, $"The disk {disk.Path} is also used by {string.Join(", ", others)}."));
            }
        }

        var warnings = new List<string>();
        if (definition.HasGpuPartition)
        {
            warnings.Add("The GPU partition is not transferred as it is. Turn Performance mode on again on the other host; the guest's GPU drivers are copied again from that host.");
        }

        if (storage.AttachedImages.Any(image => image.VmId == vmId))
        {
            warnings.Add("The DVD drive's image stays on this host; the drive is empty on the other host.");
        }

        if (_backups?.Load().Policies.Any(policy => Guid.TryParse(policy.VmId, out var id) && id == vmId) == true)
        {
            warnings.Add("The VM's backup schedule stays with this host's backup locations. Set up backups again on the other host.");
        }

        KeyProtectorHolders? holders = null;
        var ownedHere = false;
        if (definition.TpmEnabled && _keys is not null)
        {
            try
            {
                holders = await _keys.DescribeAsync(await _keys.GetAsync(vmId, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                var local = await _keys.LocalGuardianIdAsync(cancellationToken).ConfigureAwait(false);
                ownedHere = local is not null && string.Equals(holders.Owner, local, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The receiving host then asks for a grant, which fails at the export if this host does not own the key.
                warnings.Add($"This host could not read who holds the VM's TPM key ({ex.Message}).");
            }
        }

        return new TransferPreview(
            vmId,
            definition.Name,
            vm.State,
            definition.Generation,
            version,
            definition.Compute,
            definition.SecureBoot,
            definition.SecureBootTemplateId,
            definition.TpmEnabled,
            definition.HasGpuPartition,
            definition.Adapters,
            disks,
            checkpoints,
            blockers,
            warnings,
            holders,
            ownedHere);
    }
}
