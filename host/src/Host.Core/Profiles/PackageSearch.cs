using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyperHarbor.Shared.Contracts.Ipc;
using HyperHarbor.Shared.Contracts.Profiles;

namespace HyperHarbor.Host.Core.Profiles;


/// <summary>Package search cannot run on this host yet (PowerShell 7 or the WinGet module is missing).</summary>
public sealed class PackageSearchUnavailableException(string message) : Exception(message);

/// <summary>winget itself failed, for example because the source could not be reached.</summary>
public sealed class PackageSearchFailedException(string message) : Exception(message);

/// <summary>Searches the winget community source.</summary>
public interface IPackageSearch
{
    /// <exception cref="PackageSearchUnavailableException">Search is not set up on this host.</exception>
    /// <exception cref="PackageSearchFailedException">The search ran and failed.</exception>
    Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="IPackageSearch"/> through Find-WinGetPackage (Microsoft.WinGet.Client) in PowerShell 7. The service
/// runs as SYSTEM, where the module refuses to run in Windows PowerShell; verified live: pwsh 7.6 with module
/// 1.29 searched in about 2 s as SYSTEM. Results are cached for a few minutes, and at most two searches run at once.
/// </summary>
public sealed class PwshPackageSearch : IPackageSearch
{
    public const int MaxQuery = 100;
    public const int MaxCount = 50;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly string EncodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<PackageSearchResult> Results)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _running = new(2);

    public PwshPackageSearch(TimeProvider time)
    {
        _time = time;
    }

    public async Task<IReadOnlyList<PackageSearchResult>> SearchAsync(string query, int count, CancellationToken cancellationToken)
    {
        if (!PackageSearchSetupHelper.PwshInstalled || !PackageSearchSetupHelper.ModuleInstalled)
        {
            throw new PackageSearchUnavailableException(Unavailable);
        }

        var key = $"{count}|{query}";
        if (_cache.TryGetValue(key, out var cached) && _time.GetUtcNow() - cached.At < CacheFor)
        {
            return cached.Results;
        }

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = Parse(await RunAsync(new { query, count }, cancellationToken).ConfigureAwait(false));
            _cache[key] = (_time.GetUtcNow(), results);
            return results;
        }
        finally
        {
            _running.Release();
        }
    }

    private const string Unavailable =
        "Package search is not set up on this host. On the host, double-click the HyperHarbor tray icon and choose Set up package search; it installs PowerShell 7 and the WinGet PowerShell module.";

    /// <summary>The script's reply: the results, or the stage that failed (module or search).</summary>
    internal static IReadOnlyList<PackageSearchResult> Parse(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        JsonNode? reply;
        try
        {
            reply = line is null ? null : JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            reply = null;
        }

        if (reply is not JsonObject { } result || (bool?)result["ok"] is not { } ok)
        {
            throw new PackageSearchFailedException("Package search returned something unexpected.");
        }

        if (!ok)
        {
            var error = (string?)result["error"] ?? "unknown error";
            throw (string?)result["stage"] == "module"
                ? new PackageSearchUnavailableException(Unavailable)
                : new PackageSearchFailedException($"winget search failed: {error}");
        }

        return result["result"] switch
        {
            JsonArray array => array.OfType<JsonObject>()
                .Where(item => (string?)item["id"] is { Length: > 0 })
                .Select(item => new PackageSearchResult((string)item["id"]!, (string?)item["name"] ?? (string)item["id"]!, (string?)item["version"] ?? "", (string?)item["source"] ?? "winget"))
                .ToList(),
            _ => [],
        };
    }

    private static async Task<string> RunAsync(object request, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(PackageSearchSetupHelper.PwshPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", EncodedScript })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new PackageSearchFailedException("PowerShell 7 could not be started.");
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new PackageSearchFailedException("winget search did not answer in time.");
        }
    }

    /// <summary>Reads {query, count} from stdin and writes one JSON line, like the PowerShell Direct scripts.</summary>
    internal const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $request = [Console]::In.ReadToEnd() | ConvertFrom-Json
        function Reply($value) { $value | ConvertTo-Json -Compress -Depth 5 }

        try {
            Import-Module Microsoft.WinGet.Client -ErrorAction Stop
        }
        catch {
            Reply @{ ok = $false; stage = 'module'; error = $_.Exception.Message }
            exit 0
        }

        try {
            $found = @(Find-WinGetPackage -Query $request.query -Source winget -Count $request.count | ForEach-Object {
                @{ id = [string]$_.Id; name = [string]$_.Name; version = [string]$_.Version; source = [string]$_.Source }
            })
            Reply @{ ok = $true; result = $found }
        }
        catch {
            Reply @{ ok = $false; stage = 'search'; error = $_.Exception.Message }
        }
        """;
}
