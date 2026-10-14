using System.Diagnostics;
using HyperHarbor.Host.Core.Backup.Restore;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// Builds a 64 MB VHDX with one NTFS file system and a file, then mounts it read-only through <see cref="PowerShellVhdMounter"/>,
/// reads the file through <see cref="BrowsePaths"/>, and detaches it. Mount-DiskImage needs an elevated shell.
/// HH_BROWSE_LIVE=1, run from an administrator prompt.
/// </summary>
public sealed class BrowseLiveTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [EnvironmentFact("HH_BROWSE_LIVE")]
    public async Task Live_AReadOnlyMount_ListsTheVolumeAndReadsAFile()
    {
        var image = Path.Combine(_folder, "browse.vhdx");
        var make = $$"""
            $ErrorActionPreference = 'Stop'
            New-VHD -Path '{{image}}' -SizeBytes 64MB -Dynamic | Out-Null
            $disk = Mount-VHD -Path '{{image}}' -Passthru | Initialize-Disk -PartitionStyle GPT -PassThru
            $part = New-Partition -InputObject $disk -UseMaximumSize -AssignDriveLetter
            Format-Volume -Partition $part -FileSystem NTFS -NewFileSystemLabel 'HHBROWSE' -Confirm:$false | Out-Null
            Set-Content -Path ($part.DriveLetter + ':\hello.txt') -Value 'live browse'
            Dismount-VHD -Path '{{image}}'
            """;
        await RunPowerShellAsync(make);

        var mounter = new PowerShellVhdMounter();
        try
        {
            var volumes = await mounter.MountAsync(image, default);
            output.WriteLine(string.Join(", ", volumes));
            var volume = Assert.Single(volumes);
            Assert.Equal("HHBROWSE", volume.Label);
            var file = BrowsePaths.Resolve(volume.Drive, "hello.txt");
            Assert.Contains("live browse", await File.ReadAllTextAsync(file));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(Path.Combine(volume.Drive + "\\", "write.txt"), "x"));
        }
        finally
        {
            await mounter.DismountAsync(image, default);
        }
    }

    private static async Task RunPowerShellAsync(string script)
    {
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await errors);
    }
}
