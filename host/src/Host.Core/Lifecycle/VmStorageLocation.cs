namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Where a new VM's configuration and disk go.</summary>
/// <param name="ConfigurationFolder">Null: Hyper-V's default configuration folder.</param>
public sealed record VmFolders(string? ConfigurationFolder, string DiskPath);

/// <summary>
/// Decides where new VMs are stored. The folder chosen in the host tray (host-settings.json) wins over
/// Lifecycle:VmRootFolder; with neither, Hyper-V's own default folders are used. With a root folder, each
/// VM gets &lt;root&gt;\&lt;name&gt; with its disk in a "Virtual Hard Disks" subfolder. Existing VMs never move.
/// </summary>
public sealed class VmStorageLocation
{
    private readonly HostSettingsStore? _settings;
    private readonly LifecycleOptions _options;
    private readonly IHyperVHost _hyperV;

    public VmStorageLocation(HostSettingsStore? settings, LifecycleOptions options, IHyperVHost hyperV)
    {
        _settings = settings;
        _options = options;
        _hyperV = hyperV;
    }

    /// <summary>The chosen root folder, or null when Hyper-V's defaults apply.</summary>
    public string? RootFolder =>
        _settings?.VmFolder is { Length: > 0 } chosen ? chosen
        : string.IsNullOrWhiteSpace(_options.VmRootFolder) ? null
        : Path.GetFullPath(_options.VmRootFolder);

    /// <summary>The folder new VMs' disks go in (directly, or under a per-VM folder when a root is chosen).</summary>
    public async Task<string> DisplayFolderAsync(CancellationToken cancellationToken) =>
        RootFolder ?? (await _hyperV.GetDefaultsAsync(cancellationToken).ConfigureAwait(false)).VirtualHardDiskFolder;

    public async Task<VmFolders> ForAsync(string name, CancellationToken cancellationToken)
    {
        if (RootFolder is { } root)
        {
            var folder = Path.Combine(root, name);
            return new VmFolders(folder, Path.Combine(folder, "Virtual Hard Disks", name + ".vhdx"));
        }

        var defaults = await _hyperV.GetDefaultsAsync(cancellationToken).ConfigureAwait(false);
        return new VmFolders(null, Path.Combine(defaults.VirtualHardDiskFolder, name + ".vhdx"));
    }
}
