using System.Text;
using HyperHarbor.Host.Core.Updates;

namespace HyperHarbor.Host.Tests.Updates;

public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeReleaseServer _server = new();

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Destination => Path.Combine(_folder, "HyperHarbor.Host-1.2.0.exe");

    private static UpdatePackage Package(string? sha256 = null, long? size = null) =>
        new("host", "x64", Releases.PackageUrl, size ?? Releases.Package.Length, sha256 ?? Releases.Sha256(Releases.Package));

    [Fact]
    public async Task Download_ReportsProgress_FromNothingToEveryByte()
    {
        _server.Serve(Releases.PackageUrl, Releases.Package);
        var reports = new List<long>();

        await Releases.Downloader(_server).DownloadAsync(Package(), Destination, new SyncProgress<long>(reports.Add), CancellationToken.None);

        Assert.Equal(0, reports[0]);
        Assert.Equal(Releases.Package.Length, reports[^1]);
        Assert.True(reports.SequenceEqual(reports.Order()), "Progress never goes back.");
    }

    [Fact]
    public async Task Download_FollowsGitHubsRedirectToItsAssetHost_AndChecksTheHash()
    {
        _server.Redirect(Releases.PackageUrl, Releases.AssetUrl);
        _server.Serve(Releases.AssetUrl, Releases.Package);

        await Releases.Downloader(_server).DownloadAsync(Package(), Destination, CancellationToken.None);

        Assert.Equal(Releases.Package, File.ReadAllBytes(Destination));
        Assert.Equal([Releases.PackageUrl, Releases.AssetUrl], _server.Requested);
        Assert.False(File.Exists(Destination + ".partial"));
    }

    [Fact]
    public async Task Download_RefusesARedirectToAHostNotAllowed()
    {
        _server.Redirect(Releases.PackageUrl, "https://evil.example/HyperHarbor-Host-1.2.0.exe");

        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => Releases.Downloader(_server).DownloadAsync(Package(), Destination, CancellationToken.None));

        Assert.Contains("evil.example", error.Message, StringComparison.Ordinal);
        Assert.Equal([Releases.PackageUrl], _server.Requested);
    }

    [Fact]
    public async Task Download_RefusesARedirectToPlainHttp()
    {
        _server.Redirect(Releases.PackageUrl, "http://objects.githubusercontent.com/a.exe");

        await Assert.ThrowsAsync<UpdateRejectedException>(() => Releases.Downloader(_server).DownloadAsync(Package(), Destination, CancellationToken.None));
    }

    [Fact]
    public async Task Download_WithTheWrongHash_LeavesNothingBehind()
    {
        _server.Serve(Releases.PackageUrl, Releases.Package);

        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            Releases.Downloader(_server).DownloadAsync(Package(sha256: new string('0', 64)), Destination, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task Download_StopsAtTheManifestSize_WhenTheServerSendsMore()
    {
        var bigger = Releases.Package.Concat(Encoding.UTF8.GetBytes(" and a payload")).ToArray();
        _server.Serve(Releases.PackageUrl, bigger, omitLength: true);

        var error = await Assert.ThrowsAsync<UpdateRejectedException>(() => Releases.Downloader(_server).DownloadAsync(Package(), Destination, CancellationToken.None));

        Assert.Contains("larger than", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task Download_RejectsAContentLengthThatDisagreesWithTheManifest()
    {
        _server.Serve(Releases.PackageUrl, Releases.Package);

        await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            Releases.Downloader(_server).DownloadAsync(Package(size: Releases.Package.Length + 1), Destination, CancellationToken.None));
    }

    [Fact]
    public async Task Manifest_IsCappedInSize()
    {
        _server.Serve(Releases.ManifestUrl, new byte[UpdateManifest.MaxBytes + 1], omitLength: true);

        await Assert.ThrowsAsync<UpdateRejectedException>(() => Releases.Downloader(_server).GetManifestAsync(new Uri(Releases.ManifestUrl), CancellationToken.None));
    }

    [Fact]
    public async Task Manifest_FromAHostNotAllowed_IsNeverRequested()
    {
        await Assert.ThrowsAsync<UpdateRejectedException>(() =>
            Releases.Downloader(_server).GetManifestAsync(new Uri("https://example.com/latest.json"), CancellationToken.None));

        Assert.Empty(_server.Requested);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
