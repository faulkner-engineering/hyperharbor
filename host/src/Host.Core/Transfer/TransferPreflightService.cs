using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Vms;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>
/// Gathers what the receiving host knows about itself and runs <see cref="TransferPreflight"/>. The VM's folder
/// goes in the VM folder chosen in the tray, or else in Hyper-V's default configuration folder; a received VM
/// always gets a folder of its own, so a rollback never touches Hyper-V's shared default disk folder.
/// </summary>
public sealed class TransferPreflightService
{
    private readonly IVmInventory _inventory;
    private readonly VmStorageLocation _location;
    private readonly IHyperVHost _hyperV;
    private readonly IHostCapacityReader _capacity;
    private readonly IDiskFiles _files;
    private readonly IHyperVVersions _versions;
    private readonly LifecycleOptions _options;
    private readonly Func<string, IEnumerable<string>> _listFolders;
    private readonly IKeyProtectorAccess? _keys;
    private readonly ITpmCapabilityProbe? _tpm;

    /// <param name="listFolders">Names of the folders in a folder; the file system by default.</param>
    /// <param name="keys">This host's guardian, for a VM with a TPM; without it, keeping a TPM is refused.</param>
    /// <param name="tpm">What this host can do with TPMs, asked only when a VM with a TPM needs it.</param>
    public TransferPreflightService(
        IVmInventory inventory,
        VmStorageLocation location,
        IHyperVHost hyperV,
        IHostCapacityReader capacity,
        IDiskFiles files,
        IHyperVVersions versions,
        LifecycleOptions options,
        Func<string, IEnumerable<string>>? listFolders = null,
        IKeyProtectorAccess? keys = null,
        ITpmCapabilityProbe? tpm = null)
    {
        _keys = keys;
        _tpm = tpm;
        _inventory = inventory;
        _location = location;
        _hyperV = hyperV;
        _capacity = capacity;
        _files = files;
        _versions = versions;
        _options = options;
        _listFolders = listFolders ?? ListFolders;
    }

    /// <exception cref="TransferException">This host's TPM capability could not be found out.</exception>
    public async Task<TransferPlan> CheckAsync(
        TransferPreview preview, TransferMode mode, CancellationToken cancellationToken, TransferTpmMode tpmMode = TransferTpmMode.Keep)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var vms = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        var root = _location.RootFolder ?? (await _hyperV.GetDefaultsAsync(cancellationToken).ConfigureAwait(false)).ConfigurationFolder;
        var switches = await _hyperV.ListSwitchesAsync(cancellationToken).ConfigureAwait(false);
        var versions = await _versions.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var localGuardian = preview.TpmEnabled && _keys is not null ? await _keys.LocalGuardianIdAsync(cancellationToken).ConfigureAwait(false) : null;
        var tpm = TransferPreflight.NeedsTpmProbe(preview, tpmMode, localGuardian) && _tpm is not null
            ? await _tpm.ProbeAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var facts = new TransferTargetFacts(
            vms.Select(vm => (vm.Id, vm.Name)).ToList(),
            root,
            _listFolders(root).ToList(),
            _files.AvailableSpaceMb(root),
            _capacity.Read(),
            _options.HostMemoryReserveMb,
            versions,
            switches,
            localGuardian,
            tpm);
        return TransferPreflight.Evaluate(preview, mode, facts, tpmMode);
    }

    private static IEnumerable<string> ListFolders(string root) =>
        Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>() : [];
}
