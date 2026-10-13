using System.Globalization;
using HyperHarbor.Host.Core.Backup.Kopia;
using HyperHarbor.Host.Core.Lifecycle;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Core.Backup.Restore;

public sealed class RestoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Chooses a name for a restored VM that collides with nothing on the host. Pure.</summary>
public static class RestoreNaming
{
    public const string Suffix = "restored";

    /// <param name="taken">Names of existing VMs and existing folders in the destination; compared without regard to case.</param>
    public static string UniqueName(string desired, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(desired))
        {
            return desired;
        }

        var candidate = $"{desired} ({Suffix})";
        for (var n = 2; used.Contains(candidate); n++)
        {
            candidate = $"{desired} ({Suffix} {n.ToString(CultureInfo.InvariantCulture)})";
        }

        return candidate;
    }
}

/// <summary>Maps a backed-up VM's adapters to the switches this host has: by name, then the Default Switch, else unconnected.</summary>
public static class SwitchMapper
{
    public sealed record Mapping(IReadOnlyList<ImportAdapter> Adapters, IReadOnlyList<string> Warnings);

    public static Mapping Map(IReadOnlyList<ManifestAdapter> adapters, IReadOnlyList<VirtualSwitch> switches)
    {
        var mapped = new List<ImportAdapter>();
        var warnings = new List<string>();
        foreach (var adapter in adapters)
        {
            if (adapter.SwitchName is null && adapter.SwitchId is null)
            {
                mapped.Add(new ImportAdapter(null));
                continue;
            }

            var match = adapter.SwitchName is { } name
                ? switches.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                : null;
            if (match is not null)
            {
                mapped.Add(new ImportAdapter(match.Id));
                continue;
            }

            var fallback = switches.FirstOrDefault(s => s.IsDefault);
            if (fallback is not null)
            {
                mapped.Add(new ImportAdapter(fallback.Id));
                warnings.Add($"The virtual switch \"{adapter.SwitchName ?? adapter.SwitchId}\" does not exist on this host; the adapter uses \"{fallback.Name}\".");
            }
            else
            {
                mapped.Add(new ImportAdapter(null));
                warnings.Add($"The virtual switch \"{adapter.SwitchName ?? adapter.SwitchId}\" does not exist on this host and there is no Default Switch; the adapter is not connected.");
            }
        }

        return new Mapping(mapped, warnings);
    }
}

/// <summary>One VM's backups, newest first, found by the folder name of its Kopia source (the VM id).</summary>
public sealed record VmBackupSet(Guid VmId, IReadOnlyList<KopiaSnapshot> Snapshots);

public static class SnapshotCatalog
{
    /// <summary>Groups the VM repository's snapshots by VM. Snapshots whose source is not a VM staging folder are left out.</summary>
    public static IReadOnlyList<VmBackupSet> GroupByVm(IEnumerable<KopiaSnapshot> snapshots) =>
        snapshots
            .Select(s => (Id: VmIdOf(s.SourcePath), Snapshot: s))
            .Where(pair => pair.Id is not null)
            .GroupBy(pair => pair.Id!.Value)
            .Select(g => new VmBackupSet(g.Key, g.Select(p => p.Snapshot).OrderByDescending(s => s.StartTime).ToList()))
            .OrderBy(set => set.VmId)
            .ToList();

    /// <summary>The VM id in ...\.hh-backup\&lt;id as N&gt;, or null.</summary>
    public static Guid? VmIdOf(string sourcePath)
    {
        var name = sourcePath.TrimEnd('\\', '/').Split('\\', '/')[^1];
        return Guid.TryParseExact(name, "N", out var id) ? id : null;
    }
}

/// <summary>Builds the Hyper-V import plan from a manifest. Pure.</summary>
public static class ImportPlanBuilder
{
    public static ImportPlan Build(
        BackupManifest manifest, string name, string configurationFolder, IReadOnlyList<string> diskPaths, IReadOnlyList<ImportAdapter> adapters)
    {
        if (diskPaths.Count != manifest.Disks.Count)
        {
            throw new ArgumentException("There must be one restored path per disk in the manifest.");
        }

        var compute = manifest.Compute;
        return new ImportPlan(
            name,
            configurationFolder,
            manifest.Vm.Notes,
            compute.VCpu,
            compute.StartupMemoryMb,
            compute.MaxMemoryMb ?? compute.StartupMemoryMb,
            compute.DynamicMemory,
            manifest.Firmware.SecureBoot,
            manifest.Firmware.SecureBootTemplateId,
            manifest.Disks.Select((disk, i) => new ImportDisk(diskPaths[i], disk.Controller, disk.Lun)).ToList(),
            adapters);
    }
}
