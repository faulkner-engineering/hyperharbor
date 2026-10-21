using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary>
/// Spike for VM transfer (Phase 24.1): the sending host authenticates to the receiving host with its own host
/// certificate, which carries only the Server Authentication purpose. Checks that Kestrel, configured as the
/// host configures it, sees that certificate when the sender supplies it through a selection callback.
/// </summary>
public sealed class PeerHostCertificateSpikeTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-peer-spike", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task ServerAuthenticationCertificate_ReachesThePeer_ThroughTheSelectionCallback()
    {
        using var source = Certificate("source", "SOURCE-HOST");
        using var target = Certificate("target", "TARGET-HOST");
        await using var server = await StartAsync(target);

        using var client = Client(target, handler => handler.SslOptions.LocalCertificateSelectionCallback = (_, _, _, _, _) => source);
        var seen = await client.GetStringAsync(server.Urls.Single() + "/peer");

        output.WriteLine($"Selection callback: the peer saw {seen}");
        Assert.Equal(CertificateFingerprint.Of(source), seen);
    }

    [Fact]
    public async Task ServerAuthenticationCertificate_InTheCertificateCollection_IsReported()
    {
        // Informational: whether .NET's own selection offers a certificate without the Client Authentication purpose.
        using var source = Certificate("source", "SOURCE-HOST");
        using var target = Certificate("target", "TARGET-HOST");
        await using var server = await StartAsync(target);

        using var client = Client(target, handler => handler.SslOptions.ClientCertificates = new X509CertificateCollection { source });
        var seen = await client.GetStringAsync(server.Urls.Single() + "/peer");

        output.WriteLine($"Certificate collection: the peer saw {seen}");
        Assert.True(seen == "none" || seen == CertificateFingerprint.Of(source), seen);
    }

    private X509Certificate2 Certificate(string folder, string hostName)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        return new HostCertificateStore(directory, hostName).GetOrCreate();
    }

    private static async Task<WebApplication> StartAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            // As Host.Service/Program.cs configures it.
            https.ServerCertificate = certificate;
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        })));

        var app = builder.Build();
        app.MapGet("/peer", (HttpContext context) =>
            context.Connection.ClientCertificate is { } peer ? CertificateFingerprint.Of(peer) : "none");
        await app.StartAsync();
        return app;
    }

    private static HttpClient Client(X509Certificate2 pinned, Action<SocketsHttpHandler> configure)
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null && certificate.GetCertHashString() == pinned.GetCertHashString(),
            },
        };
        configure(handler);
        return new HttpClient(handler) { DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };
    }
}
