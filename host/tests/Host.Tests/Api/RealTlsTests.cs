using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Users;
using HyperHarbor.Shared.Contracts.Pairing;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>
/// Starts the real service executable on a loopback port and talks to it over TLS, which TestServer
/// cannot do. This covers the Kestrel setup: HTTPS only, TLS 1.2 or later, the host certificate, and
/// client certificates checked by fingerprint.
/// </summary>
public sealed class RealTlsTests : IClassFixture<RealTlsTests.ServiceProcess>
{
    private readonly ServiceProcess _service;

    public RealTlsTests(ServiceProcess service) => _service = service;

    [Fact]
    public async Task PairedCertificate_IsAccepted()
    {
        using var client = _service.CreateClient(_service.PairedCertificate);

        var response = await client.GetAsync("/api/v1/host");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoCertificate_IsRejected()
    {
        using var client = _service.CreateClient(certificate: null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/host")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/vms")).StatusCode);
    }

    [Fact]
    public async Task UnpairedCertificate_IsRejected()
    {
        using var certificate = TestHost.CreateClientCertificate("Unpaired");
        using var client = _service.CreateClient(certificate);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/host")).StatusCode);
    }

    [Fact]
    public async Task PairingHandshake_IsReachableWithoutCertificate()
    {
        using var client = _service.CreateClient(certificate: null);
        using var certificate = TestHost.CreateClientCertificate("New Device");

        var response = await client.PostAsJsonAsync("/api/v1/pairing/requests", new PairingRequest("New Device", certificate.ExportCertificatePem()));

        // No tray is connected to the test pipe, so the host cannot show a PIN. Reaching that check
        // shows the request passed authentication.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ServerCertificate_IsTheStoredHostCertificate()
    {
        X509Certificate2? presented = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                presented = certificate is null ? null : new X509Certificate2(certificate);
                return true;
            },
        };
        using var client = new HttpClient(handler) { BaseAddress = _service.BaseAddress };

        await client.GetAsync("/api/v1/host");

        Assert.NotNull(presented);
        Assert.Equal(_service.HostCertificateFingerprint, CertificateFingerprint.Of(presented!));
    }

    [Fact]
    public async Task Handshake_NegotiatesTls12OrLater()
    {
        await using var stream = await _service.ConnectTlsAsync(SslProtocols.None);

        Assert.True(stream.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13, $"Negotiated {stream.SslProtocol}.");
    }

    [Theory]
#pragma warning disable SYSLIB0039, CA5397 // Old protocols are named on purpose: the server must refuse them.
    [InlineData(SslProtocols.Tls11)]
    [InlineData(SslProtocols.Tls)]
#pragma warning restore SYSLIB0039, CA5397
    public async Task Handshake_RefusesOldProtocols(SslProtocols protocol)
    {
        // Fails on either side: the server allows only TLS 1.2 and 1.3, and current Windows clients
        // disable these too. Either way, no session is established.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var stream = await _service.ConnectTlsAsync(protocol);
        });
    }

    [Fact]
    public async Task PlainHttp_GetsNoResponse()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_service.Port}") };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("/api/v1/host"));
    }

    /// <summary>One service process for the class, with a paired certificate written before it starts.</summary>
    public sealed class ServiceProcess : IAsyncLifetime
    {
        private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

        private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "hyperharbor-tests", Guid.NewGuid().ToString("N"));
        private readonly StringBuilder _output = new();
        private Process? _process;

        public X509Certificate2 PairedCertificate { get; } = TestHost.CreateClientCertificate("Paired");

        public string HostCertificateFingerprint { get; private set; } = string.Empty;

        public int Port { get; } = FreePort();

        public Uri BaseAddress => new($"https://127.0.0.1:{Port}");

        public async Task InitializeAsync()
        {
            // The service loads these at startup, so they are written first.
            var users = new UserStore(_dataDirectory);
            var user = users.GetOrCreateDefault();
            new PairedDeviceStore(_dataDirectory, users).Add(user.UserId, "Paired", CertificateFingerprint.Of(PairedCertificate), DateTimeOffset.UtcNow);
            using (var hostCertificate = new HostCertificateStore(_dataDirectory, Environment.MachineName).GetOrCreate())
            {
                HostCertificateFingerprint = CertificateFingerprint.Of(hostCertificate);
            }

            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "HyperHarbor.Host.Service.exe"))
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in new[]
            {
                "--contentRoot", AppContext.BaseDirectory,
                "--DataDirectory", _dataDirectory,
                "--Api:Port", Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "--Api:ListenAddress", "127.0.0.1",
                "--Discovery:Enabled", "false",
                "--Tray:PipeName", "HyperHarbor.Tests." + Guid.NewGuid().ToString("N"),
            })
            {
                start.ArgumentList.Add(argument);
            }

            _process = Process.Start(start) ?? throw new InvalidOperationException("The service did not start.");
            _process.OutputDataReceived += (_, e) => Append(e.Data);
            _process.ErrorDataReceived += (_, e) => Append(e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var deadline = DateTime.UtcNow + StartTimeout;
            while (true)
            {
                if (_process.HasExited)
                {
                    throw new InvalidOperationException($"The service exited with code {_process.ExitCode}:\n{Output()}");
                }

                try
                {
                    using var probe = new TcpClient();
                    await probe.ConnectAsync(IPAddress.Loopback, Port);
                    return;
                }
                catch (SocketException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(200);
                }
                catch (SocketException ex)
                {
                    throw new TimeoutException($"The service did not listen on port {Port}:\n{Output()}", ex);
                }
            }
        }

        public async Task DisposeAsync()
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process?.Dispose();
            PairedCertificate.Dispose();
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // The process may still hold a file for a moment; the directory is under %TEMP%.
            }
        }

        /// <summary>A client that trusts the host certificate by fingerprint, as the real client does.</summary>
        public HttpClient CreateClient(X509Certificate2? certificate)
        {
            var handler = new HttpClientHandler
            {
                ClientCertificateOptions = ClientCertificateOption.Manual,
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented is not null && CertificateFingerprint.Of(presented) == HostCertificateFingerprint,
            };
            if (certificate is not null)
            {
                handler.ClientCertificates.Add(WithExportablePrivateKey(certificate));
            }

            return new HttpClient(handler, disposeHandler: true) { BaseAddress = BaseAddress };
        }

        public async Task<SslStream> ConnectTlsAsync(SslProtocols protocols)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, Port);
            var stream = new SslStream(
                tcp.GetStream(),
                leaveInnerStreamOpen: false,
                (_, presented, _, _) => presented is not null && CertificateFingerprint.Of(new X509Certificate2(presented)) == HostCertificateFingerprint);
            try
            {
                await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = protocols,
                });
                return stream;
            }
            catch
            {
                await stream.DisposeAsync();
                tcp.Dispose();
                throw;
            }
        }

        /// <summary>
        /// SChannel needs a persisted key to send a client certificate; an ephemeral key from
        /// CreateSelfSigned works only after a PFX round trip.
        /// </summary>
        private static X509Certificate2 WithExportablePrivateKey(X509Certificate2 certificate) =>
            new(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);

        private void Append(string? line)
        {
            if (line is not null)
            {
                lock (_output)
                {
                    _output.AppendLine(line);
                }
            }
        }

        private string Output()
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
