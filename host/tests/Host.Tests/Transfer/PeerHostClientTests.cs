using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HyperHarbor.Host.Core.Security;
using HyperHarbor.Host.Core.Transfer;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Tests.Transfer;

/// <summary><see cref="PeerHostClient"/> against a Kestrel configured as the host configures it, with stand-in routes.</summary>
public sealed class PeerHostClientTests : IAsyncLifetime
{
    private static readonly Guid TransferId = Guid.Parse("5a0c1d2e-3f40-4152-8364-758697a8b9ca");
    private const string Ticket = "the-ticket";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hyperharbor-peer-client", Guid.NewGuid().ToString("N"));
    private readonly List<string> _seen = [];
    private X509Certificate2 _source = null!;
    private X509Certificate2 _target = null!;
    private WebApplication _server = null!;
    private int _port;

    /// <summary>The next answer of each route, as status code and body.</summary>
    private (int Status, string Body) _next = (200, string.Empty);

    public async Task InitializeAsync()
    {
        _source = new HostCertificateStore(Path.Combine(_root, "source"), "SOURCE").GetOrCreate();
        _target = new HostCertificateStore(Path.Combine(_root, "target"), "TARGET").GetOrCreate();

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
        {
            https.ServerCertificate = _target;
            https.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            https.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            https.AllowAnyClientCertificate();
        })));
        _server = builder.Build();
        _server.Map("/api/v1/transfers/{id}/{**rest}",async (HttpContext context) =>
        {
            var request = context.Request;
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            var client = context.Connection.ClientCertificate is { } certificate ? CertificateFingerprint.Of(certificate) : "none";
            _seen.Add($"{request.Method} {request.Path} auth={request.Headers.Authorization} client={client} range={request.Headers.ContentRange} digest={request.Headers["Content-Digest"]} body={Convert.ToHexString(body.ToArray())}");
            context.Response.StatusCode = _next.Status;
            if (_next.Body.Length > 0)
            {
                context.Response.ContentType = _next.Status < 300 ? "application/json" : "application/problem+json";
                await context.Response.WriteAsync(_next.Body);
            }
        });
        await _server.StartAsync();
        _port = new Uri(_server.Urls.Single()).Port;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        _source.Dispose();
        _target.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Status_TriesTheAddressesInOrder_PresentsTheHostCertificate_AndSendsTheTicket()
    {
        _next = (200, """{"transferId":"5a0c1d2e-3f40-4152-8364-758697a8b9ca","state":"receiving","files":[{"key":"disk:0","size":10,"received":8}]}""");
        using var channel = Open(["::1", "127.0.0.1"]);

        var status = await channel.StatusAsync(CancellationToken.None);

        Assert.Equal(IncomingTransferState.Receiving, status.State);
        Assert.Equal(8, status.Received["disk:0"]);
        Assert.Equal($"GET /api/v1/transfers/{TransferId:D} auth=HyperHarbor-Transfer {Ticket} client={CertificateFingerprint.Of(_source)} range= digest= body=", Assert.Single(_seen));
    }

    [Fact]
    public async Task Block_CarriesItsRangeAndDigest()
    {
        using var channel = Open(["127.0.0.1"]);
        var block = "4567"u8.ToArray();
        _next = (200, """{"transferId":"5a0c1d2e-3f40-4152-8364-758697a8b9ca","state":"receiving","files":[]}""");
        await channel.StatusAsync(CancellationToken.None);
        _next = (200, string.Empty);

        await channel.SendBlockAsync("definition:0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b.vmcx", 4, 4, 10, SHA256.HashData(block), new MemoryStream(block), CancellationToken.None);

        Assert.Equal(
            $"PUT /api/v1/transfers/{TransferId:D}/files/definition:0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b.vmcx auth=HyperHarbor-Transfer {Ticket} client={CertificateFingerprint.Of(_source)} "
            + $"range=bytes 4-7/10 digest=sha-256=:{Convert.ToBase64String(SHA256.HashData(block))}: body={Convert.ToHexString(block)}",
            _seen[1]);
    }

    [Fact]
    public async Task AnOffsetConflict_SaysWhereTheNextBlockStarts()
    {
        using var channel = Open(["127.0.0.1"]);
        _next = (409, """{"title":"Wrong offset","status":409,"code":"transferOffset","expectedOffset":8}""");

        var error = await Assert.ThrowsAsync<TransferOffsetException>(() => channel.ImportAsync(CancellationToken.None));

        Assert.Equal(8, error.Expected);
    }

    [Fact]
    public async Task Import_ReadsTheImportedVm()
    {
        using var channel = Open(["127.0.0.1"]);
        _next = (200, """{"vmId":"0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b","name":"Web (copy)","warnings":["Set it up again."]}""");

        var result = await channel.ImportAsync(CancellationToken.None);

        Assert.Equal((Guid.Parse("0b9a6f53-1c2d-4e8f-a1b2-3c4d5e6f7a8b"), "Web (copy)"), (result.VmId, result.Name));
        Assert.Equal(["Set it up again."], result.Warnings);
        Assert.StartsWith($"POST /api/v1/transfers/{TransferId:D}/import", _seen.Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "", typeof(TransferException), "ticket ended")]
    [InlineData(400, """{"title":"Bad block","detail":"The block's SHA-256 does not match."}""", typeof(TransferException), "SHA-256 does not match")]
    [InlineData(503, "", typeof(PeerTransportException), "busy")]
    public async Task Refusals_AreTransferErrors_AndBusyIsWorthRetrying(int status, string body, Type expected, string message)
    {
        using var channel = Open(["127.0.0.1"]);
        _next = (status, body);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => channel.CompleteAsync(CancellationToken.None));

        Assert.IsType(expected, error);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatIsNotThePairedOne_IsRefused_NotRetried()
    {
        using var channel = new PeerHostClient(() => _source).Open(Request(["127.0.0.1"], Convert.ToHexString(SHA256.HashData("someone else"u8))));

        await Assert.ThrowsAsync<TransferException>(() => channel.CancelAsync(CancellationToken.None));
        Assert.Empty(_seen);
    }

    [Fact]
    public async Task NoReachableAddress_IsATransportError()
    {
        using var channel = Open(["::1"]);

        await Assert.ThrowsAsync<PeerTransportException>(() => channel.StatusAsync(CancellationToken.None));
    }

    private IPeerTransferChannel Open(string[] addresses) =>
        new PeerHostClient(() => _source).Open(Request(addresses, CertificateFingerprint.Of(_target)));

    private TransferSendRequest Request(string[] addresses, string fingerprint) => new(
        Guid.NewGuid(), TransferMode.Move, TransferId, Ticket, new PeerEndpoint(Guid.NewGuid(), "LAPTOP", addresses, _port, fingerprint), null);
}
