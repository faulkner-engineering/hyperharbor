using System.Diagnostics;
using HyperHarbor.Host.Core.Backup;
using HyperHarbor.Host.Core.Backup.Restore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Backup;

public sealed class SnapshotBrowserTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _volume;
    private readonly string _outside;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeRepositories _repositories;
    private readonly FakeRestorer _restorer = new();
    private readonly FakeMounter _mounter;
    private readonly SnapshotBrowser _browser;
    private readonly BackupTarget _target = new() { Id = "t1", Name = "Local", Kind = BackupTargetKind.Local, Local = new LocalTargetSettings(@"D:\B") };

    public SnapshotBrowserTests()
    {
        _volume = Directory.CreateDirectory(Path.Combine(_root, "volume")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "host secret");
        Directory.CreateDirectory(Path.Combine(_volume, "Users", "me"));
        File.WriteAllText(Path.Combine(_volume, "Users", "me", "notes.txt"), "hello");
        File.WriteAllText(Path.Combine(_volume, "readme.txt"), "root file");
        _repositories = new FakeRepositories(_root);
        _mounter = new FakeMounter(_volume);
        _restorer.Manifest = Manifest();
        _browser = new SnapshotBrowser(_repositories, _restorer, _mounter, _ => Task.FromResult(Path.Combine(_root, "work")), _time, NullLogger<SnapshotBrowser>.Instance);
    }

    public void Dispose()
    {
        // Junctions must go before the recursive delete could follow them.
        foreach (var link in Directory.EnumerateDirectories(_volume).Where(d => new DirectoryInfo(d).LinkTarget is not null))
        {
            Directory.Delete(link);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static BackupManifest Manifest() => new()
    {
        HyperHarborVersion = "1.0.0",
        CreatedAt = new DateTimeOffset(2026, 10, 12, 2, 0, 0, TimeSpan.Zero),
        Vm = new ManifestVm(Guid.NewGuid(), "Dev", 2, null),
        Compute = new ManifestCompute(1, 1024, null, false, false, false),
        Firmware = new ManifestFirmware(true, null),
        Tpm = false,
        Network = [],
        Disks = [new ManifestDisk("disk-0.vhdx", "scsi", 0, 0, 3)],
        Gpu = null,
        Performance = new ManifestPerformance(false, null, null),
        Consistency = new ManifestConsistency(ConsistencyMode.Off, []),
        Guest = new ManifestGuest("Windows", false),
    };

    private async Task<BrowseSessionInfo> OpenReadyAsync()
    {
        var info = _browser.Open(_target, "snap-1");
        for (var i = 0; i < 500 && _browser.Get(info.Id)!.State == BrowseState.Preparing; i++)
        {
            await Task.Delay(10);
        }

        return _browser.Get(info.Id)!;
    }

    [Fact]
    public async Task Open_RestoresMountsAndShowsTheVolumes()
    {
        var info = await OpenReadyAsync();

        Assert.Equal(BrowseState.Ready, info.State);
        Assert.Equal("Dev", info.VmName);
        Assert.Equal(_volume, Assert.Single(info.Volumes).Drive);
        Assert.EndsWith("disk-0.vhdx", Assert.Single(_mounter.Mounted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAndOpenFile_ReadTheVolume_FoldersFirst()
    {
        var info = await OpenReadyAsync();
        var drive = info.Volumes[0].Drive;

        var root = _browser.List(info.Id, drive, null);
        var folder = _browser.List(info.Id, drive, "Users/me");
        using var file = _browser.OpenFile(info.Id, drive, @"Users\me\notes.txt");
        using var reader = new StreamReader(file);

        Assert.Equal(["Users", "readme.txt"], root.Select(e => e.Name));
        Assert.True(root[0].IsDirectory);
        Assert.Equal(5, Assert.Single(folder).Size);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\outside\secret.txt")]
    [InlineData("Users/../../outside")]
    [InlineData("readme.txt:stream")]
    [InlineData("C:\\Windows")]
    [InlineData("NUL")]
    [InlineData("com1.txt")]
    public async Task APathThatEscapesOrIsOdd_IsRefused(string path)
    {
        var info = await OpenReadyAsync();

        Assert.Throws<RestoreException>(() => _browser.List(info.Id, info.Volumes[0].Drive, path));
        Assert.Throws<RestoreException>(() => _browser.OpenFile(info.Id, info.Volumes[0].Drive, path));
    }

    [Fact]
    public async Task AJunctionInsideTheDisk_IsNeitherListedNorFollowed()
    {
        var link = Path.Combine(_volume, "escape");
        var made = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_outside}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        await made.WaitForExitAsync();
        Assert.True(Directory.Exists(link));
        var info = await OpenReadyAsync();
        var drive = info.Volumes[0].Drive;

        Assert.DoesNotContain(_browser.List(info.Id, drive, null), e => e.Name == "escape");
        Assert.Throws<RestoreException>(() => _browser.List(info.Id, drive, "escape"));
        Assert.Throws<RestoreException>(() => _browser.OpenFile(info.Id, drive, "escape/secret.txt"));
    }

    [Fact]
    public async Task AnUnknownVolumeOrSession_IsRefused()
    {
        var info = await OpenReadyAsync();

        Assert.Throws<RestoreException>(() => _browser.List(info.Id, "Z:", null));
        Assert.Throws<RestoreException>(() => _browser.List("nope", info.Volumes[0].Drive, null));
    }

    [Fact]
    public async Task OnlyOneSessionAtATime()
    {
        await OpenReadyAsync();

        Assert.Throws<BackupException>(() => _browser.Open(_target, "snap-2"));
    }

    [Fact]
    public async Task Close_DetachesTheDisk_AndRemovesTheWorkFolder()
    {
        var info = await OpenReadyAsync();
        var work = Directory.EnumerateDirectories(Path.Combine(_root, "work"), SnapshotBrowser.FolderPrefix + "*").Single();

        await _browser.CloseAsync(info.Id);

        Assert.Equal(_mounter.Mounted, _mounter.Dismounted);
        Assert.False(Directory.Exists(work));
        Assert.Null(_browser.Get(info.Id));
        Assert.Equal(BrowseState.Ready, (await OpenReadyAsync()).State);
    }

    [Fact]
    public async Task ASessionNotUsedFor30Minutes_Expires_AndUseKeepsItAlive()
    {
        var info = await OpenReadyAsync();
        _time.Advance(TimeSpan.FromMinutes(20));
        _browser.List(info.Id, info.Volumes[0].Drive, null);
        _time.Advance(TimeSpan.FromMinutes(20));
        await _browser.CloseExpiredAsync();
        Assert.NotNull(_browser.Get(info.Id));

        _time.Advance(TimeSpan.FromMinutes(31));
        await _browser.CloseExpiredAsync();

        Assert.Null(_browser.Get(info.Id));
        Assert.Equal(_mounter.Mounted, _mounter.Dismounted);
    }

    [Fact]
    public async Task ADiskWithoutAReadableVolume_Fails_WithAReason_AndCleansUp()
    {
        _mounter.Volumes = false;

        var info = await OpenReadyAsync();

        Assert.Equal(BrowseState.Failed, info.State);
        Assert.Contains("No volume", info.Error);
        Assert.Equal(_mounter.Mounted, _mounter.Dismounted);
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, "work")));
    }

    [Fact]
    public async Task ARepositoryFailure_IsShownAsAFailedSession()
    {
        _repositories.Throw = new BackupException("The passphrase is missing.");

        var info = await OpenReadyAsync();

        Assert.Equal(BrowseState.Failed, info.State);
        Assert.Equal("The passphrase is missing.", info.Error);
    }

    [Fact]
    public async Task LeftoversOfAnEarlierRun_AreDetachedAndDeleted()
    {
        var left = Directory.CreateDirectory(Path.Combine(_root, "work", SnapshotBrowser.FolderPrefix + "old"));
        File.WriteAllBytes(Path.Combine(left.FullName, "disk-0.vhdx"), [1]);

        await _browser.CleanUpLeftoversAsync(default);

        Assert.False(Directory.Exists(left.FullName));
        Assert.Single(_mounter.Dismounted);
    }

    private sealed class FakeMounter(string volume) : IVhdMounter
    {
        public bool Volumes { get; set; } = true;

        public List<string> Mounted { get; } = [];

        public List<string> Dismounted { get; } = [];

        public Task<IReadOnlyList<BrowseVolume>> MountAsync(string imagePath, CancellationToken cancellationToken)
        {
            Mounted.Add(imagePath);
            return Task.FromResult<IReadOnlyList<BrowseVolume>>(Volumes ? [new BrowseVolume(volume, "Windows", 1000)] : []);
        }

        public Task DismountAsync(string imagePath, CancellationToken cancellationToken)
        {
            Dismounted.Add(imagePath);
            return Task.CompletedTask;
        }
    }
}
