using System.IO.Compression;
using System.Net;
using HyperHarbor.Host.Core.Profiles;
using HyperHarbor.Shared.Contracts.Profiles;
using Microsoft.Extensions.Time.Testing;

namespace HyperHarbor.Host.Tests.Profiles;

public sealed class WingetIndexTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"hh-winget-{Guid.NewGuid():N}");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));

    public WingetIndexTests()
    {
        Directory.CreateDirectory(_folder);
    }

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

    [Fact]
    public void Search_PutsExactThenPrefixThenContainsMatchesFirst_AndMatchesTags()
    {
        var index = BuildIndex(Path.Combine(_folder, "index.db"));

        var vlc = WingetIndex.Search(index, "vlc", 10);
        var editors = WingetIndex.Search(index, "text-editor", 10);
        var limited = WingetIndex.Search(index, "o", 2);

        Assert.Equal(["VideoLAN.VLC", "CrySer66.JellyfinVlcBridge"], vlc.Select(result => result.Id));
        Assert.Equal(new PackageSearchResult("VideoLAN.VLC", "VLC media player", "3.0.24", "winget"), vlc[0]);
        Assert.Equal(["Notepad++.Notepad++"], editors.Select(result => result.Id));
        Assert.Equal(2, limited.Count);
    }

    [Fact]
    public void Search_TreatsLikeWildcardsAsText()
    {
        var index = BuildIndex(Path.Combine(_folder, "index.db"));

        Assert.Empty(WingetIndex.Search(index, "%", 10));
        Assert.Equal(["Notepad++.Notepad++"], WingetIndex.Search(index, "++", 10).Select(result => result.Id));
    }

    [Fact]
    public async Task CurrentAsync_DownloadsAndUnpacksTheSource_ThenReusesIt()
    {
        var source = Source(BuildIndex(Path.Combine(_folder, "built.db")));
        var cdn = new FakeCdn(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(source) });
        var data = Path.Combine(_folder, "data");
        var index = new WingetIndex(new HttpClient(cdn), data, _time);

        var first = await index.CurrentAsync(CancellationToken.None);
        var second = await index.CurrentAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Equal(1, cdn.Requests);
        Assert.Equal("VideoLAN.VLC", WingetIndex.Search(first!, "vlc", 1)[0].Id);
        Assert.Equal([first], Directory.GetFiles(Path.Combine(data, WingetIndex.FolderName)));
    }

    [Fact]
    public async Task CurrentAsync_WithoutTheInternet_IsNull_AndDoesNotRetryAtOnce()
    {
        var cdn = new FakeCdn(() => throw new HttpRequestException("offline"));
        var index = new WingetIndex(new HttpClient(cdn), Path.Combine(_folder, "data"), _time);

        Assert.Null(await index.CurrentAsync(CancellationToken.None));
        Assert.Null(await index.CurrentAsync(CancellationToken.None));
        Assert.Equal(1, cdn.Requests);

        _time.Advance(TimeSpan.FromMinutes(6));
        Assert.Null(await index.CurrentAsync(CancellationToken.None));
        Assert.Equal(2, cdn.Requests);
    }

    [Fact]
    public async Task CurrentAsync_RefusesAnIndexInAnotherFormat()
    {
        var path = BuildIndex(Path.Combine(_folder, "v1.db"), majorVersion: "1");
        var cdn = new FakeCdn(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Source(path)) });
        var data = Path.Combine(_folder, "data");

        Assert.Null(await new WingetIndex(new HttpClient(cdn), data, _time).CurrentAsync(CancellationToken.None));
        Assert.Empty(Directory.GetFiles(Path.Combine(data, WingetIndex.FolderName)));
    }

    [Fact]
    public async Task Search_FallsBackToPowerShell_WhenThereIsNoIndex()
    {
        var cdn = new FakeCdn(() => throw new HttpRequestException("offline"));
        var index = new WingetIndex(new HttpClient(cdn), Path.Combine(_folder, "data"), _time);
        var fallback = new FixedSearch([new PackageSearchResult("Git.Git", "Git", "2.47.0", "winget")]);

        var found = await new WingetPackageSearch(index, fallback).SearchAsync("git", 5, CancellationToken.None);

        Assert.Equal("Git.Git", Assert.Single(found).Id);
    }

    [Fact]
    public async Task Search_WithNeitherIndexNorPowerShell_SaysWhatIsMissing()
    {
        var cdn = new FakeCdn(() => throw new HttpRequestException("offline"));
        var index = new WingetIndex(new HttpClient(cdn), Path.Combine(_folder, "data"), _time);

        var error = await Assert.ThrowsAsync<PackageSearchUnavailableException>(() =>
            new WingetPackageSearch(index, new FixedSearch(null)).SearchAsync("git", 5, CancellationToken.None));

        Assert.Contains("cdn.winget.microsoft.com", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A package index in the source2 format with a few packages.</summary>
    private static string BuildIndex(string path, string majorVersion = "2")
    {
        using var db = WinSqlite.Open(path, readOnly: false);
        db.Execute("CREATE TABLE metadata (name TEXT PRIMARY KEY NOT NULL, value TEXT NOT NULL)");
        db.Execute("CREATE TABLE packages (rowid INTEGER PRIMARY KEY, id TEXT NOT NULL, name TEXT NOT NULL, moniker TEXT, latest_version TEXT NOT NULL)");
        db.Execute("CREATE TABLE tags2 (rowid INTEGER PRIMARY KEY, tag TEXT NOT NULL)");
        db.Execute("CREATE TABLE tags2_map (tag INT64 NOT NULL, package INT64 NOT NULL, PRIMARY KEY (tag, package))");
        db.Execute("INSERT INTO metadata VALUES ('majorVersion', ?1)", majorVersion);
        string[][] packages =
        [
            ["CrySer66.JellyfinVlcBridge", "Jellyfin VLC Bridge", "jellyfin-vlc-bridge", "1.18.0"],
            ["VideoLAN.VLC", "VLC media player", "vlc", "3.0.24"],
            ["Notepad++.Notepad++", "Notepad++", "notepad++", "8.7"],
            ["Mozilla.Firefox", "Mozilla Firefox", "firefox", "131.0"],
        ];
        foreach (var package in packages)
        {
            db.Execute("INSERT INTO packages (id, name, moniker, latest_version) VALUES (?1, ?2, ?3, ?4)", package[0], package[1], package[2], package[3]);
        }

        db.Execute("INSERT INTO tags2 (rowid, tag) VALUES (1, 'text-editor')");
        db.Execute("INSERT INTO tags2_map VALUES (1, 3)");
        return path;
    }

    /// <summary>An msix (a zip) holding the index as Public/index.db.</summary>
    private static byte[] Source(string indexPath)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntryFromFile(indexPath, "Public/index.db");
            zip.CreateEntry("AppxManifest.xml");
        }

        return buffer.ToArray();
    }

    private sealed class FakeCdn(Func<HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal(WingetIndex.SourceUri, request.RequestUri);
            return Task.FromResult(reply());
        }
    }

    /// <summary>The PowerShell search: these results, or not set up when null.</summary>
    private sealed class FixedSearch(IReadOnlyList<PackageSearchResult>? results) : IPackageSearch
    {
        public Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken) =>
            results is null ? throw new PackageSearchUnavailableException("not set up") : Task.FromResult(results);
    }
}
