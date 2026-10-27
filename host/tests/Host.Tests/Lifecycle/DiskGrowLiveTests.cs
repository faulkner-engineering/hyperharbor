using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

/// <summary>
/// Creates a 1 GB VHDX in a temporary folder through Hyper-V, grows it to 2 GB with the real
/// <see cref="CimHyperVDisks"/>, and reads it back. No VM is involved. Opt-in:
/// HH_DISK_GROW_LIVE=1 dotnet test --filter DiskGrowLiveTests
/// </summary>
public sealed class DiskGrowLiveTests : IDisposable
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-live", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [EnvironmentFact("HH_DISK_GROW_LIVE")]
    public async Task ADisk_GrowsAndReportsItsNewSize()
    {
        var path = Path.Combine(_root, "grow.vhdx");
        await new CimHyperVBuilder().CreateDiskAsync(path, 1 * Gb, _ => { }, CancellationToken.None);
        var disks = new CimHyperVDisks();

        Assert.Equal(new VirtualDiskInfo(1 * Gb, IsVhdx: true, ParentPath: null), await disks.ReadAsync(path, CancellationToken.None));

        var progress = new List<int>();
        await disks.ResizeAsync(path, 2 * Gb, progress.Add, CancellationToken.None);

        Assert.Equal(2 * Gb, (await disks.ReadAsync(path, CancellationToken.None))!.MaximumSizeBytes);
        Assert.Null(await disks.ReadAsync(Path.Combine(_root, "missing.vhdx"), CancellationToken.None));
    }
}
