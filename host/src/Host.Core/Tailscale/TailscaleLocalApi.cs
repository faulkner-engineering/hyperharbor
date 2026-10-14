using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Host.Core.GameAware;
using HyperHarbor.Host.Core.Installation;

namespace HyperHarbor.Host.Core.Tailscale;

/// <summary>
/// Where the Tailscale client is reached, in tailscale.json. The defaults suit Tailscale's own control server; a
/// Headscale user sets the control server and, when the client listens elsewhere, the local API port.
/// </summary>
public sealed record TailscaleSettings
{
    public const int DefaultLocalApiPort = 41112;

    public int LocalApiPort { get; init; } = DefaultLocalApiPort;

    /// <summary>The control server URL, or null for Tailscale's own. Only used to build the "tailscale up" command.</summary>
    public string? ControlUrl { get; init; }

    /// <summary>Whether a value is usable as a control server URL: absolute http or https, no spaces, quotes, or control characters.</summary>
    public static bool IsValidControlUrl(string? value) =>
        value is { Length: > 0 and <= 300 }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http"
        && string.IsNullOrEmpty(uri.UserInfo)
        && value.All(character => character is > ' ' and < '\u007f' && character is not ('"' or '\'' or '`' or '&' or '|' or ';' or '<' or '>' or '^' or '%' or '$' or '(' or ')' or '\\'));

    public static bool IsValidPort(int port) => port is >= 1 and <= 65535;
}

/// <summary>tailscale.json, written atomically. Nothing in it is secret.</summary>
public sealed class TailscaleSettingsStore
{
    public const string FileName = "tailscale.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public TailscaleSettingsStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _path = Path.Combine(dataDirectory, FileName);
    }

    public TailscaleSettings Load()
    {
        lock (_gate)
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<TailscaleSettings>(Utf8Json.ReadFile(_path), Json) ?? new TailscaleSettings()
                : new TailscaleSettings();
        }
    }

    /// <exception cref="ArgumentException">The port or the control URL is not valid.</exception>
    public TailscaleSettings Save(TailscaleSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TailscaleSettings.IsValidPort(settings.LocalApiPort))
        {
            throw new ArgumentException("The local API port must be between 1 and 65535.", nameof(settings));
        }

        var url = string.IsNullOrWhiteSpace(settings.ControlUrl) ? null : settings.ControlUrl.Trim();
        if (url is not null && !TailscaleSettings.IsValidControlUrl(url))
        {
            throw new ArgumentException("The control server must be an http or https URL.", nameof(settings));
        }

        var clean = settings with { ControlUrl = url };
        lock (_gate)
        {
            AtomicJson.Write(_path, clean, Json);
        }

        return clean;
    }
}

public enum TailscaleReplyKind
{
    Ok,
    NotInstalled,
    Failed,
}

/// <param name="Body">The JSON answer when <paramref name="Kind"/> is Ok.</param>
public sealed record TailscaleReply(TailscaleReplyKind Kind, string? Body = null, string? Error = null)
{
    public static TailscaleReply Ok(string body) => new(TailscaleReplyKind.Ok, body);

    public static TailscaleReply Failed(string error) => new(TailscaleReplyKind.Failed, Error: error);
}

/// <summary>One way to ask the local Tailscale client: its local API over HTTP, or its command line.</summary>
public interface ITailscaleLocalApi
{
    Task<TailscaleReply> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Resolves a tailnet address (without a port) to its node.</summary>
    Task<TailscaleReply> WhoIsAsync(string address, CancellationToken cancellationToken);
}

/// <summary>The local API over TCP on the loopback address (port 41112 by default, from <see cref="TailscaleSettings"/>).</summary>
public sealed class TailscaleHttpLocalApi(HttpClient http, Func<TailscaleSettings> settings) : ITailscaleLocalApi
{
    public Task<TailscaleReply> GetStatusAsync(CancellationToken cancellationToken) => GetAsync("status", cancellationToken);

    public Task<TailscaleReply> WhoIsAsync(string address, CancellationToken cancellationToken) =>
        GetAsync($"whois?addr={Uri.EscapeDataString(address)}", cancellationToken);

    private async Task<TailscaleReply> GetAsync(string route, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{settings().LocalApiPort}/localapi/v0/{route}");
            request.Headers.Add("Sec-Tailscale", "localapi");
            using var response = await http.SendAsync(request, timeout.Token);
            return response.IsSuccessStatusCode
                ? TailscaleReply.Ok(await response.Content.ReadAsStringAsync(timeout.Token))
                : TailscaleReply.Failed($"The Tailscale local API answered {(int)response.StatusCode}.");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return TailscaleReply.Failed("The Tailscale local API did not answer.");
        }
    }
}

/// <summary>Runs a program and returns what it wrote. A seam so tests need no Tailscale.</summary>
public interface ITailscaleProcess
{
    /// <returns>Null when the program does not exist.</returns>
    Task<TailscaleProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

public sealed record TailscaleProcessResult(int ExitCode, string Output, string Error);

/// <summary>
/// "tailscale status --json" and "tailscale whois --json". On Windows the client's own local API is a named pipe, so
/// the command line is the dependable route there.
/// </summary>
public sealed class TailscaleCliLocalApi(ITailscaleProcess process, Func<IEnumerable<string>> candidates) : ITailscaleLocalApi
{
    /// <summary>The usual locations, then the PATH.</summary>
    public static IEnumerable<string> DefaultCandidates()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (!string.IsNullOrEmpty(root))
            {
                yield return Path.Combine(root, "Tailscale", "tailscale.exe");
            }
        }

        yield return "tailscale";
    }

    public Task<TailscaleReply> GetStatusAsync(CancellationToken cancellationToken) => RunAsync(["status", "--json"], cancellationToken);

    public Task<TailscaleReply> WhoIsAsync(string address, CancellationToken cancellationToken) => RunAsync(["whois", "--json", address], cancellationToken);

    private async Task<TailscaleReply> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates())
        {
            if (Path.IsPathRooted(candidate) && !File.Exists(candidate))
            {
                continue;
            }

            var result = await process.RunAsync(candidate, arguments, cancellationToken);
            if (result is null)
            {
                continue;
            }

            // "tailscale status --json" exits 1 with valid JSON while Tailscale is stopped; the JSON is what matters.
            if (result.Output.TrimStart().StartsWith('{'))
            {
                return TailscaleReply.Ok(result.Output);
            }

            return TailscaleReply.Failed(FirstLine(result.Error) ?? FirstLine(result.Output) ?? $"tailscale exited with {result.ExitCode}.");
        }

        return new TailscaleReply(TailscaleReplyKind.NotInstalled);
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() is { Length: > 0 } line
            ? line[..Math.Min(line.Length, 200)]
            : null;
}

/// <summary>Starts the real program with no window and without a shell.</summary>
public sealed class TailscaleProcess : ITailscaleProcess
{
    public async Task<TailscaleProcessResult?> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process started;
        try
        {
            started = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }

        using (started)
        {
            started.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = started.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = started.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await started.WaitForExitAsync(timeout.Token);
                return new TailscaleProcessResult(started.ExitCode, await output, await error);
            }
            catch (OperationCanceledException)
            {
                started.Kill(entireProcessTree: true);
                if (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }

                return new TailscaleProcessResult(-1, string.Empty, "tailscale did not answer in time.");
            }
        }
    }
}
