using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using HyperHarbor.Host.Core.Elevation;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Tests.Live;

/// <summary>
/// The real host executable from this build, on a loopback port, with its own data folder: a test pairing and a
/// test admin passphrase are written there first, so a live test drives the API like a client without touching the
/// installed host's data. The folder (HH_HARNESS_DATA, default %LOCALAPPDATA%\HyperHarborHarness) is kept between
/// runs, so VMs it created keep their stored credentials. VMs go to HH_HARNESS_VM_FOLDER and images come from
/// HH_HARNESS_ISO_FOLDER.
/// </summary>
public sealed class LiveHostHarness : IAsyncDisposable
{
    public const string Passphrase = "harness passphrase for live tests";
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly X509Certificate2 _certificate;
    private readonly string _log;
    private string? _token;

    private LiveHostHarness(Process process, X509Certificate2 certificate, string dataDirectory, int port, string log)
    {
        _process = process;
        _certificate = certificate;
        DataDirectory = dataDirectory;
        Port = port;
        _log = log;
        Client = CreateClient();
    }

    public string DataDirectory { get; }

    public int Port { get; }

    public HttpClient Client { get; }

    public static async Task<LiveHostHarness> StartAsync(Action<string>? progress = null)
    {
        var data = Environment.GetEnvironmentVariable("HH_HARNESS_DATA") is { Length: > 0 } configured
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HyperHarborHarness");
        Directory.CreateDirectory(data);

        // The harness certificate is kept with the data, so the pairing stays valid between runs. It is always
        // loaded from the file: Windows TLS cannot use the ephemeral key of a certificate made in memory.
        var certificatePath = Path.Combine(data, "harness-client.pfx");
        if (!File.Exists(certificatePath))
        {
            using var created = TestHost.CreateClientCertificate("Live harness");
            await File.WriteAllBytesAsync(certificatePath, created.Export(X509ContentType.Pfx));
            var users = new UserStore(data);
            new PairedDeviceStore(data, users).Add(users.GetOrCreateDefault().UserId, "Live harness", CertificateFingerprint.Of(created), DateTimeOffset.UtcNow);
        }

        var certificate = new X509Certificate2(certificatePath, (string?)null, X509KeyStorageFlags.UserKeySet);

        var passphrases = new AdminPassphraseStore(data);
        if (!passphrases.Verify(Passphrase))
        {
            var hash = Shared.Contracts.Ipc.AdminPassphrase.CreateHash(Passphrase, Elevation.ElevationServiceTests.TestIterations);
            passphrases.Set(hash.Salt, hash.Hash, hash.Iterations);
        }

        var port = FreePort();
        var log = Path.Combine(data, $"host-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "HyperHarbor.Host.exe"))
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var arguments = new List<string>
        {
            "run",
            "--contentRoot", AppContext.BaseDirectory,
            "--DataDirectory", data,
            "--Api:Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--Api:ListenAddress", "127.0.0.1",
            "--Discovery:Enabled", "false",
            "--KeepAwake:Enabled", "false",
            "--Tray:PipeName", "HyperHarbor.Harness." + Guid.NewGuid().ToString("N"),
        };
        if (Environment.GetEnvironmentVariable("HH_HARNESS_ISO_FOLDER") is { Length: > 0 } isos)
        {
            arguments.AddRange(["--Lifecycle:IsoFolder", isos]);
        }

        if (Environment.GetEnvironmentVariable("HH_HARNESS_VM_FOLDER") is { Length: > 0 } vms)
        {
            arguments.AddRange(["--Lifecycle:VmRootFolder", vms]);
        }

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var process = Process.Start(start) ?? throw new InvalidOperationException("The host did not start.");
        var writer = TextWriter.Synchronized(new StreamWriter(log, append: true) { AutoFlush = true });
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) writer.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) writer.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        progress?.Invoke($"Host started (pid {process.Id}, port {port}, data {data}, log {log}).");

        var deadline = DateTime.UtcNow + StartTimeout;
        while (true)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"The host exited with code {process.ExitCode}; see {log}.");
            }

            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, port);
                break;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }

        return new LiveHostHarness(process, certificate, data, port, log);
    }

    /// <summary>Sends a request with an elevation token (asked for once, again when it expires).</summary>
    public async Task<HttpResponseMessage> SendElevatedAsync(HttpMethod method, string path, object? body = null)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_token is null)
            {
                var elevate = await Client.PostAsJsonAsync("/api/v1/auth/elevation", new { passphrase = Passphrase });
                elevate.EnsureSuccessStatusCode();
                _token = (string)(await elevate.Content.ReadFromJsonAsync<JsonObject>())!["token"]!;
            }

            using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: ContractJson.Options) };
            request.Headers.Add(ContractInfo.ElevationHeader, _token);
            var response = await Client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.Forbidden)
            {
                return response;
            }

            _token = null;
        }

        throw new InvalidOperationException("Elevation was refused twice.");
    }

    public async Task<JsonNode> GetJsonAsync(string path) =>
        (await Client.GetFromJsonAsync<JsonNode>(path))!;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
        _certificate.Dispose();
    }

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            // The harness's host certificate is self-signed and new to this machine; the test trusts its own host.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };
        handler.ClientCertificates.Add(_certificate);
        return new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{Port}"), Timeout = TimeSpan.FromMinutes(20) };
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
