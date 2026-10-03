using System.Runtime.InteropServices;
using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Hosts;

namespace HyperHarbor.Host.Core.Lifecycle;

/// <summary>Settings for VM creation and changes. Bound from the "Lifecycle" configuration section.</summary>
public sealed class LifecycleOptions
{
    public const string SectionName = "Lifecycle";

    /// <summary>
    /// Folder for new VMs: each gets &lt;root&gt;\&lt;name&gt; with its disk in a "Virtual Hard Disks" subfolder.
    /// Unset: Hyper-V's own default folders.
    /// </summary>
    public string? VmRootFolder { get; set; }

    /// <summary>The ISO library. Unset: "HyperHarbor ISOs" in the Public Documents folder.</summary>
    public string? IsoFolder { get; set; }

    /// <summary>Warn when a VM would leave the host with less free memory than this.</summary>
    public long HostMemoryReserveMb { get; set; } = 4096;

    /// <summary>How long a shut-down-and-apply job waits for the guest to shut down.</summary>
    public int ShutdownTimeoutSeconds { get; set; } = 300;

    public string EffectiveIsoFolder => string.IsNullOrWhiteSpace(IsoFolder)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "HyperHarbor ISOs")
        : Path.GetFullPath(IsoFolder);
}

/// <summary>Processor and memory figures for validating VM settings.</summary>
public sealed record HostCapacity(int LogicalProcessorCount, long TotalMemoryMb, long AvailableMemoryMb);

public interface IHostCapacityReader
{
    HostCapacity Read();
}

/// <summary>Logical processors from the runtime; memory from GlobalMemoryStatusEx.</summary>
public sealed class WindowsHostCapacityReader : IHostCapacityReader
{
    public HostCapacity Read()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status))
        {
            throw new InvalidOperationException("The host's memory status could not be read.");
        }

        return new HostCapacity(Environment.ProcessorCount, (long)(status.TotalPhys / (1024 * 1024)), (long)(status.AvailPhys / (1024 * 1024)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}

/// <summary>Hyper-V's default folders for new VMs.</summary>
public sealed record HyperVDefaults(string ConfigurationFolder, string VirtualHardDiskFolder);

/// <summary>Host-level Hyper-V facts used when creating VMs.</summary>
public interface IHyperVHost
{
    Task<HyperVDefaults> GetDefaultsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<VirtualSwitch>> ListSwitchesAsync(CancellationToken cancellationToken);
}

/// <summary><see cref="IHyperVHost"/> through root\virtualization\v2.</summary>
public sealed class CimHyperVHost : IHyperVHost
{
    /// <summary>The Name of the NAT "Default Switch" on client editions of Windows.</summary>
    public const string DefaultSwitchId = "C08CB7B8-9B3C-408E-8E30-5E16A3AEB444";

    public Task<HyperVDefaults> GetDefaultsAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            using var settings = HyperVCim.QuerySingle(session, "SELECT * FROM Msvm_VirtualSystemManagementServiceSettingData")
                ?? throw new HyperVUnavailableException("The Hyper-V management service settings were not found.");
            return Task.FromResult(new HyperVDefaults(
                settings.CimInstanceProperties["DefaultExternalDataRoot"]?.Value as string ?? @"C:\ProgramData\Microsoft\Windows\Hyper-V",
                settings.CimInstanceProperties["DefaultVirtualHardDiskPath"]?.Value as string ?? @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks"));
        }, cancellationToken);

    public Task<IReadOnlyList<VirtualSwitch>> ListSwitchesAsync(CancellationToken cancellationToken) =>
        HyperVCim.RunAsync(session =>
        {
            var switches = new List<VirtualSwitch>();
            foreach (var instance in HyperVCim.Query(session, "SELECT Name, ElementName FROM Msvm_VirtualEthernetSwitch"))
            {
                using (instance)
                {
                    if (instance.CimInstanceProperties["Name"]?.Value is string id)
                    {
                        var name = instance.CimInstanceProperties["ElementName"]?.Value as string ?? id;
                        switches.Add(new VirtualSwitch(id, name, string.Equals(id, DefaultSwitchId, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<VirtualSwitch>>(switches
                .OrderByDescending(item => item.IsDefault)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList());
        }, cancellationToken);
}

/// <summary>
/// Lists and resolves installation images in the ISO library folder. Clients pick an image by its
/// name relative to the folder; a name can never resolve outside it, including through a junction
/// or symbolic link.
/// </summary>
public sealed class IsoLibrary
{
    /// <summary>Subfolders deeper than this are not listed.</summary>
    public const int MaxDepth = 4;

    public IsoLibrary(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Folder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
    }

    public string Folder { get; }

    /// <summary>The images in the library, sorted by name. Creates the folder if it is missing.</summary>
    public IReadOnlyList<IsoImage> List()
    {
        try
        {
            Directory.CreateDirectory(Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var images = new List<IsoImage>();
        Collect(new DirectoryInfo(Folder), 0, images);
        return images.OrderBy(image => image.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The full path of the image named <paramref name="name"/>.</summary>
    /// <exception cref="LifecycleValidationException">The name is not an image in the library.</exception>
    public string Resolve(string name)
    {
        static LifecycleValidationException Invalid(string message) =>
            new(message, [new ValidationIssue("isoName", message)]);

        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':', StringComparison.Ordinal))
        {
            throw Invalid("Choose an image from the host's ISO library.");
        }

        var segments = name.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw Invalid("Choose an image from the host's ISO library.");
        }

        var full = Path.GetFullPath(Path.Combine([Folder, .. segments]));
        if (!full.StartsWith(Folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(full), ".iso", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("Choose an image from the host's ISO library.");
        }

        // Every folder and the file itself must be real, so a link cannot point outside the library.
        var current = Folder;
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            var info = new FileInfo(current);
            if (!info.Exists && !Directory.Exists(current))
            {
                throw Invalid($"\"{name}\" is not in the ISO library.");
            }

            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                throw Invalid($"\"{name}\" is a link; only files stored in the ISO library can be used.");
            }
        }

        if (!File.Exists(full))
        {
            throw Invalid($"\"{name}\" is not in the ISO library.");
        }

        return full;
    }

    private void Collect(DirectoryInfo directory, int depth, List<IsoImage> images)
    {
        IEnumerable<FileSystemInfo> entries;
        try
        {
            entries = directory.EnumerateFileSystemInfos().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (entry is DirectoryInfo subfolder)
            {
                if (depth < MaxDepth)
                {
                    Collect(subfolder, depth + 1, images);
                }
            }
            else if (entry is FileInfo file && string.Equals(file.Extension, ".iso", StringComparison.OrdinalIgnoreCase))
            {
                images.Add(new IsoImage(Path.GetRelativePath(Folder, file.FullName), file.Length, file.LastWriteTimeUtc));
            }
        }
    }
}
