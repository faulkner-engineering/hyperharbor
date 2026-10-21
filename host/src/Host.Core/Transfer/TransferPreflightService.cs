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

    /// <param name="listFolders">Names of the folders in a folder; the file system by default.</param>
    public TransferPreflightService(
        IVmInventory inventory,
        VmStorageLocation location,
        IHyperVHost hyperV,
        IHostCapacityReader capacity,
        IDiskFiles files,
        IHyperVVersions versions,
        LifecycleOptions options,
        Func<string, IEnumerable<string>>? listFolders = null)
    {
        _inventory = inventory;
        _location = location;
        _hyperV = hyperV;
        _capacity = capacity;
        _files = files;
        _versions = versions;
        _options = options;
        _listFolders = listFolders ?? ListFolders;
    }

    public async Task<TransferPlan> CheckAsync(TransferPreview preview, TransferMode mode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var vms = await _inventory.ListAsync(cancellationToken).ConfigureAwait(false);
        var root = _location.RootFolder ?? (await _hyperV.GetDefaultsAsync(cancellationToken).ConfigureAwait(false)).ConfigurationFolder;
        var switches = await _hyperV.ListSwitchesAsync(cancellationToken).ConfigureAwait(false);
        var versions = await _versions.ReadCapabilitiesAsync(cancellationToken).ConfigureAwait(false);

        var facts = new TransferTargetFacts(
            vms.Select(vm => (vm.Id, vm.Name)).ToList(),
            root,
            _listFolders(root).ToList(),
            _files.AvailableSpaceMb(root),
            _capacity.Read(),
            _options.HostMemoryReserveMb,
            versions,
            switches);
        return TransferPreflight.Evaluate(preview, mode, facts);
    }

    private static IEnumerable<string> ListFolders(string root) =>
        Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>() : [];
}
