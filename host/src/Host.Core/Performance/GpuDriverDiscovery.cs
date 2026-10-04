using HyperHarbor.Host.Core.HyperV;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Management.Infrastructure;

namespace HyperHarbor.Host.Core.Performance;

/// <summary>A host GPU driver as it must appear in a GPU-P guest.</summary>
/// <param name="DriverStoreFolders">Host folders under DriverStore\FileRepository, copied whole to the guest's HostDriverStore\FileRepository.</param>
/// <param name="WindowsFiles">Host files under the Windows folder (System32, SysWOW64), as paths relative to it, copied to the same place in the guest.</param>
/// <param name="WindowsFolders">Whole folders under the Windows folder, relative to it, copied to the same place.</param>
public sealed record GpuDriverPackage(
    GpuVendor Vendor,
    string Version,
    IReadOnlyList<string> DriverStoreFolders,
    IReadOnlyList<string> WindowsFiles,
    IReadOnlyList<string> WindowsFolders);

/// <summary>The host GPU's driver cannot be copied into a guest.</summary>
public sealed class GpuDriverException(string message) : Exception(message);

/// <summary>Finds the files of the host GPU's driver package.</summary>
public interface IGpuDriverSource
{
    /// <exception cref="GpuDriverException">The vendor is not supported or the driver could not be found.</exception>
    Task<GpuDriverPackage> ReadAsync(HostGpuInfo gpu, CancellationToken cancellationToken);
}

/// <summary>
/// Sorts a driver's files into what a GPU-P guest needs. Files in the DriverStore are copied as their
/// whole FileRepository folder, because the guest's paravirtual driver loads the user-mode parts from
/// HostDriverStore; other files under System32 or SysWOW64 go to the same place in the guest. NVIDIA
/// drivers also need every nv*.dll in System32 and the "drivers\NVIDIA Corporation" folder. Vendors
/// other than NVIDIA, AMD, and Intel are refused.
/// </summary>
public static class GpuDriverClassifier
{
    private const string FileRepository = @"System32\DriverStore\FileRepository\";

    /// <param name="files">Full host paths of the driver's files.</param>
    /// <param name="windowsFolder">The host's Windows folder, for example C:\Windows.</param>
    /// <param name="system32NvidiaFiles">NVIDIA only: names of nv*.dll files present in the host's System32.</param>
    /// <param name="nvidiaCorporationFolderExists">NVIDIA only: whether System32\drivers\NVIDIA Corporation exists on the host.</param>
    public static GpuDriverPackage Classify(
        GpuVendor vendor,
        string version,
        IEnumerable<string> files,
        string windowsFolder,
        IEnumerable<string>? system32NvidiaFiles = null,
        bool nvidiaCorporationFolderExists = false)
    {
        if (vendor is not (GpuVendor.Nvidia or GpuVendor.Amd or GpuVendor.Intel))
        {
            throw new GpuDriverException("Performance mode copies NVIDIA, AMD, and Intel GPU drivers only.");
        }

        var windows = windowsFolder.TrimEnd('\\') + @"\";
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var windowsFiles = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!file.StartsWith(windows, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = file[windows.Length..];
            if (relative.StartsWith(FileRepository, StringComparison.OrdinalIgnoreCase))
            {
                var folder = relative[FileRepository.Length..].Split('\\')[0];
                if (folder.Length > 0)
                {
                    folders.Add(Path.Combine(windows, FileRepository, folder));
                }
            }
            else if (relative.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
            {
                windowsFiles.Add(relative);
            }
        }

        var windowsFolders = new List<string>();
        if (vendor == GpuVendor.Nvidia)
        {
            foreach (var name in system32NvidiaFiles ?? [])
            {
                windowsFiles.Add(@"System32\" + name);
            }

            if (nvidiaCorporationFolderExists)
            {
                windowsFolders.Add(@"System32\drivers\NVIDIA Corporation");
            }
        }

        if (folders.Count == 0)
        {
            throw new GpuDriverException("The GPU driver has no files in the driver store, so it cannot be copied into a guest.");
        }

        return new GpuDriverPackage(vendor, version, [.. folders], [.. windowsFiles], windowsFolders);
    }
}

/// <summary>
/// Reads the driver's files for any vendor: the display driver's own DriverStore folder from
/// Win32_VideoController.InstalledDisplayDrivers, plus every file of the device's driver package from the
/// Win32_PnPSignedDriverCIMDataFile association (root\cimv2), the approach Easy-GPU-PV uses. That
/// association cannot be followed from one driver (Win32_PnPSignedDriver has no usable key), so all of it
/// is enumerated and filtered by device ID; this takes about a minute, which is why it runs in a job.
/// </summary>
public sealed class CimGpuDriverSource : IGpuDriverSource
{
    public Task<GpuDriverPackage> ReadAsync(HostGpuInfo gpu, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            using var session = CimSession.Create(null);
            var id = HyperVCim.Escape(gpu.PnpDeviceId);
            using var driver = session.QueryInstances(@"root\cimv2", HyperVCim.QueryDialect,
                    $"SELECT DeviceID, DriverVersion FROM Win32_PnPSignedDriver WHERE DeviceID = '{id}'").FirstOrDefault()
                ?? throw new GpuDriverException($"No signed driver was found for {gpu.Name}.");
            var version = driver.CimInstanceProperties["DriverVersion"]?.Value as string ?? gpu.DriverVersion
                ?? throw new GpuDriverException($"The driver version of {gpu.Name} is unknown.");

            var files = new List<string>();
            using (var controller = session.QueryInstances(@"root\cimv2", HyperVCim.QueryDialect,
                $"SELECT InstalledDisplayDrivers FROM Win32_VideoController WHERE PNPDeviceID = '{id}'").FirstOrDefault())
            {
                if (controller?.CimInstanceProperties["InstalledDisplayDrivers"]?.Value is string installed)
                {
                    files.AddRange(installed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
            }

            foreach (var link in session.EnumerateInstances(@"root\cimv2", "Win32_PNPSignedDriverCIMDataFile"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (link)
                {
                    if (link.CimInstanceProperties["Antecedent"]?.Value is CimInstance antecedent
                        && string.Equals(antecedent.CimInstanceProperties["DeviceID"]?.Value as string, gpu.PnpDeviceId, StringComparison.OrdinalIgnoreCase)
                        && link.CimInstanceProperties["Dependent"]?.Value is CimInstance dependent
                        && dependent.CimInstanceProperties["Name"]?.Value is string path)
                    {
                        files.Add(path);
                    }
                }
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var system32 = Path.Combine(windows, "System32");
            var nvidiaFiles = gpu.Vendor == GpuVendor.Nvidia
                ? Directory.EnumerateFiles(system32, "nv*.dll").Select(Path.GetFileName).OfType<string>().ToList()
                : [];
            var nvidiaFolder = gpu.Vendor == GpuVendor.Nvidia && Directory.Exists(Path.Combine(system32, "drivers", "NVIDIA Corporation"));
            return GpuDriverClassifier.Classify(gpu.Vendor, version, files, windows, nvidiaFiles, nvidiaFolder);
        }, cancellationToken);
}
