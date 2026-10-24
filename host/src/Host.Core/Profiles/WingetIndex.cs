using System.IO.Compression;
using HyperHarbor.Shared.Contracts.Profiles;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Core.Profiles;

/// <summary>
/// The winget community source's package index, kept on the host: Microsoft publishes it as source2.msix (a zip
/// holding Public/index.db, a SQLite file of about 9 MB with every package's id, name, moniker, latest version, and
/// tags). It is downloaded on first use and again once a day, over HTTPS from Microsoft's CDN only, and read with
/// Windows' own SQLite. Each download goes to its own file, so a search reading the old one is never disturbed.
/// </summary>
public sealed class WingetIndex
{
    public const string FolderName = "winget";
    public static readonly Uri SourceUri = new("https://cdn.winget.microsoft.com/cache/source2.msix");

    private const long MaxDownloadBytes = 128L * 1024 * 1024;
    private const string IndexEntry = "Public/index.db";
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http;
    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _downloading = new(1);
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;

    public WingetIndex(HttpClient http, string dataDirectory, TimeProvider time, ILogger<WingetIndex>? logger = null)
    {
        _http = http;
        _folder = Path.Combine(dataDirectory, FolderName);
        _time = time;
        _logger = logger;
    }

    public static HttpClient CreateHttpClient(string userAgent)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>
    /// The index file to read, downloading it first when there is none. A stale one is returned at once and
    /// refreshed in the background. Null when there is none and it cannot be downloaded now.
    /// </summary>
    public async Task<string?> CurrentAsync(CancellationToken cancellationToken)
    {
        var current = Newest();
        if (current is null)
        {
            return await DownloadAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_time.GetUtcNow() - File.GetLastWriteTimeUtc(current) > RefreshAfter)
        {
            _ = Task.Run(() => DownloadAsync(CancellationToken.None), CancellationToken.None);
        }

        return current;
    }

    /// <summary>
    /// Packages whose id, name, or moniker contains the query, or with the query as a tag (also with its spaces as
    /// hyphens, the usual tag spelling, so "photo editor" finds the photo-editor tag); best matches first.
    /// </summary>
    public static IReadOnlyList<PackageSearchResult> Search(string indexPath, string query, int count)
    {
        var text = query.Trim();
        var lower = text.ToLowerInvariant();
        var contains = $"%{EscapeLike(text)}%";
        var prefix = $"{EscapeLike(lower)}%";
        var tag = string.Join('-', lower.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        const string sql = """
            SELECT id, name, latest_version FROM packages
            WHERE id LIKE ?1 ESCAPE '\' OR name LIKE ?1 ESCAPE '\' OR moniker LIKE ?1 ESCAPE '\'
               OR rowid IN (SELECT m.package FROM tags2_map m JOIN tags2 t ON t.rowid = m.tag WHERE t.tag IN (?2, ?5))
            ORDER BY CASE
                WHEN lower(id) = ?2 OR lower(name) = ?2 OR moniker = ?2 THEN 0
                WHEN lower(name) LIKE ?3 ESCAPE '\' OR moniker LIKE ?3 ESCAPE '\' THEN 1
                WHEN id LIKE ?1 ESCAPE '\' OR name LIKE ?1 ESCAPE '\' OR moniker LIKE ?1 ESCAPE '\' THEN 2
                ELSE 3 END,
                length(name), name
            LIMIT ?4
            """;
        using var db = WinSqlite.Open(indexPath);
        return db.Query(sql, 3, contains, lower, prefix, count, tag)
            .Where(row => !string.IsNullOrEmpty(row[0]))
            .Select(row => new PackageSearchResult(row[0]!, row[1] ?? row[0]!, row[2] ?? "", "winget"))
            .ToList();
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>Downloads and unpacks a new index. Null when that fails; failures are not retried for a few minutes.</summary>
    private async Task<string?> DownloadAsync(CancellationToken cancellationToken)
    {
        await _downloading.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have finished a download while this one waited.
            var newest = Newest();
            if (newest is not null && _time.GetUtcNow() - File.GetLastWriteTimeUtc(newest) <= RefreshAfter)
            {
                return newest;
            }

            if (_time.GetUtcNow() - _lastFailure < RetryAfter)
            {
                return newest;
            }

            try
            {
                var path = await FetchAsync(cancellationToken).ConfigureAwait(false);
                DeleteOlderThan(path);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or SqliteException or TaskCanceledException or UnauthorizedAccessException)
            {
                _lastFailure = _time.GetUtcNow();
                _logger?.LogWarning(ex, "The winget package index could not be downloaded from {Source}.", SourceUri);
                return newest;
            }
        }
        finally
        {
            _downloading.Release();
        }
    }

    private async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_folder);
        var stamp = _time.GetUtcNow().UtcTicks;
        var package = Path.Combine(_folder, $"source-{stamp}.msix.partial");
        var extracted = Path.Combine(_folder, $"index-{stamp}.db.partial");
        var target = Path.Combine(_folder, $"index-{stamp}.db");
        try
        {
            using (var response = await _http.GetAsync(SourceUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxDownloadBytes)
                {
                    throw new InvalidDataException("The winget source is larger than expected.");
                }

                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var file = File.Create(package);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > MaxDownloadBytes)
                    {
                        throw new InvalidDataException("The winget source is larger than expected.");
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }

            using (var archive = ZipFile.OpenRead(package))
            {
                var entry = archive.GetEntry(IndexEntry) ?? throw new InvalidDataException("The winget source has no package index.");
                if (entry.Length > MaxDownloadBytes)
                {
                    throw new InvalidDataException("The winget package index is larger than expected.");
                }

                entry.ExtractToFile(extracted, overwrite: true);
            }

            using (var db = WinSqlite.Open(extracted))
            {
                var major = db.Query("SELECT value FROM metadata WHERE name = 'majorVersion'", 1).FirstOrDefault()?[0];
                if (major != "2")
                {
                    throw new InvalidDataException($"The winget package index has format {major ?? "unknown"}; HyperHarbor reads format 2.");
                }
            }

            File.Move(extracted, target);
            return target;
        }
        finally
        {
            TryDelete(package);
            TryDelete(extracted);
        }
    }

    private string? Newest() =>
        Directory.Exists(_folder)
            ? Directory.EnumerateFiles(_folder, "index-*.db").OrderByDescending(path => path, StringComparer.Ordinal).FirstOrDefault()
            : null;

    /// <summary>Removes earlier indexes; one still open by a search is left for the next download.</summary>
    private void DeleteOlderThan(string keep)
    {
        foreach (var path in Directory.EnumerateFiles(_folder, "index-*.db").Where(path => !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase)))
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// <see cref="IPackageSearch"/> over the downloaded <see cref="WingetIndex"/>, so search needs nothing installed on
/// the host. When the index cannot be had (no download yet and no internet), it falls back to
/// <see cref="PwshPackageSearch"/>.
/// </summary>
public sealed class WingetPackageSearch : IPackageSearch
{
    private readonly WingetIndex _index;
    private readonly IPackageSearch _fallback;
    private readonly ILogger? _logger;

    public WingetPackageSearch(WingetIndex index, IPackageSearch fallback, ILogger<WingetPackageSearch>? logger = null)
    {
        _index = index;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        if (await _index.CurrentAsync(cancellationToken).ConfigureAwait(false) is { } path)
        {
            try
            {
                return WingetIndex.Search(path, query, count);
            }
            catch (Exception ex) when (ex is SqliteException or DllNotFoundException or EntryPointNotFoundException)
            {
                _logger?.LogWarning(ex, "The winget package index at {Path} could not be searched.", path);
            }
        }

        try
        {
            return await _fallback.SearchAsync(query, count, cancellationToken).ConfigureAwait(false);
        }
        catch (PackageSearchUnavailableException)
        {
            throw new PackageSearchUnavailableException(
                "Package search needs the winget package index, which the host downloads from cdn.winget.microsoft.com, and it could not be downloaded. Check the host's internet connection, or set up PowerShell search from the host's tray icon (Set up package search).");
        }
    }
}
