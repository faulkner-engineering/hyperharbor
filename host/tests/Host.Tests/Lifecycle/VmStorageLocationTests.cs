using HyperHarbor.Host.Core.Lifecycle;

namespace HyperHarbor.Host.Tests.Lifecycle;

public sealed class VmStorageLocationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeHyperVHost _hyperV = new();

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task WithNothingChosen_HyperVDefaultsApply()
    {
        var location = new VmStorageLocation(new HostSettingsStore(_directory), new LifecycleOptions(), _hyperV);

        var folders = await location.ForAsync("Dev", CancellationToken.None);

        Assert.Null(location.RootFolder);
        Assert.Null(folders.ConfigurationFolder);
        Assert.Equal(@"C:\Hyper-V\Virtual Hard Disks\Dev.vhdx", folders.DiskPath);
        Assert.Equal(@"C:\Hyper-V\Virtual Hard Disks", await location.DisplayFolderAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConfiguredRoot_GivesEachVmItsOwnFolder()
    {
        var location = new VmStorageLocation(new HostSettingsStore(_directory), new LifecycleOptions { VmRootFolder = @"E:\VMs" }, _hyperV);

        var folders = await location.ForAsync("Dev", CancellationToken.None);

        Assert.Equal(@"E:\VMs\Dev", folders.ConfigurationFolder);
        Assert.Equal(@"E:\VMs\Dev\Virtual Hard Disks\Dev.vhdx", folders.DiskPath);
    }

    [Fact]
    public async Task FolderChosenInTheTray_WinsOverConfiguration_AndAppliesAtOnce()
    {
        var settings = new HostSettingsStore(_directory);
        var location = new VmStorageLocation(settings, new LifecycleOptions { VmRootFolder = @"E:\VMs" }, _hyperV);

        settings.SetVmFolder(@"F:\Hyper-V");

        Assert.Equal(@"F:\Hyper-V", location.RootFolder);
        Assert.Equal(@"F:\Hyper-V\Dev\Virtual Hard Disks\Dev.vhdx", (await location.ForAsync("Dev", CancellationToken.None)).DiskPath);
        Assert.Equal(@"F:\Hyper-V", new HostSettingsStore(_directory).VmFolder);
    }

    [Fact]
    public void BothFolders_AreKeptIndependently()
    {
        var settings = new HostSettingsStore(_directory);

        settings.SetIsoFolder(@"D:\ISOs");
        settings.SetVmFolder(@"F:\Hyper-V");

        var reloaded = new HostSettingsStore(_directory);
        Assert.Equal(@"D:\ISOs", reloaded.IsoFolder);
        Assert.Equal(@"F:\Hyper-V", reloaded.VmFolder);
    }
}
