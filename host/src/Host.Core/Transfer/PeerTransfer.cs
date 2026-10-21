using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Vms;
using Microsoft.Win32.SafeHandles;

namespace HyperHarbor.Host.Core.Transfer;

/// <summary>The receiving host's view of a transfer: its state and the verified bytes of each file it has.</summary>
public sealed record PeerTransferStatus(IncomingTransferState State, IReadOnlyDictionary<string, long> Received);

/// <summary>The other host could not be reached, or the connection broke; worth retrying.</summary>
public sealed class PeerTransportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The data requests of one transfer, as the sending host makes them to the receiving host.</summary>
public interface IPeerTransferChannel : IDisposable
{
    Task<PeerTransferStatus> StatusAsync(CancellationToken cancellationToken);

    /// <exception cref="TransferOffsetException">The receiving host expects the block at another offset.</exception>
    /// <exception cref="PeerTransportException">The block may or may not have arrived.</exception>
    Task SendBlockAsync(string fileKey, long offset, long length, long fileSize, byte[] sha256, Stream block, CancellationToken cancellationToken);

    /// <summary>Safe to repeat: the receiving host answers with the same VM once it is imported.</summary>
    Task<TransferImportResult> ImportAsync(CancellationToken cancellationToken);

    Task CompleteAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

/// <summary>Opens the channel a transfer job sends through.</summary>
public interface IPeerTransferChannelFactory
{
    IPeerTransferChannel Open(TransferSendRequest request);
}

/// <summary>
/// Opens transfer channels to other hosts over HTTPS. Each request pins the receiving host's certificate by its
/// SHA-256 fingerprint and presents this host's own certificate, which the receiving host checks against the
/// fingerprint the authorizing client gave it, together with the transfer's ticket. The routes are the transferTicket
/// operations of docs/api.yaml.
/// </summary>
public sealed class PeerHostClient : IPeerTransferChannelFactory
{
    public const string AuthorizationScheme = ContractInfo.TransferTicketScheme;

    private readonly Func<X509Certificate2> _certificate;
    private readonly TimeSpan _connectTimeout;

    /// <param name="certificate">This host's certificate, with its private key.</param>
    public PeerHostClient(Func<X509Certificate2> certificate, TimeSpan? connectTimeout = null)
    {
        _certificate = certificate;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(10);
    }

    public IPeerTransferChannel Open(TransferSendRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pinned = TransferReceiver.NormalizeFingerprint(request.Target.Fingerprint)
            ?? throw new TransferException($"The certificate fingerprint of {request.Target.Name} is not valid.");
        var certificate = _certificate();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = _connectTimeout,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    presented is not null && Convert.ToHexString(SHA256.HashData(presented.GetRawCertData())) == pinned,

                // The host certificate carries only the Server Authentication purpose, which .NET's own client certificate
                // selection skips; the callback presents it anyway (verified in PeerHostCertificateSpikeTests).
                LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate,
            },
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(AuthorizationScheme, request.Ticket);
        return new HttpPeerTransferChannel(client, request);
    }
}

/// <summary>One transfer's requests over a pinned HTTPS client. The first address that answers is kept.</summary>
internal sealed class HttpPeerTransferChannel(HttpClient client, TransferSendRequest request) : IPeerTransferChannel
{
    private static readonly TimeSpan ShortRequest = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan BlockRequest = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private Uri? _base;

    public async Task<PeerTransferStatus> StatusAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Path()), ShortRequest, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync<StatusBody>(response, cancellationToken).ConfigureAwait(false);
        return new PeerTransferStatus(body.State, body.Files.ToDictionary(file => file.Key, file => file.Received, StringComparer.Ordinal));
    }

    public async Task SendBlockAsync(string fileKey, long offset, long length, long fileSize, byte[] sha256, Stream block, CancellationToken cancellationToken)
    {
        // A block's body can be sent only once, so it goes only to an address that has just answered.
        if (_base is null)
        {
            await StatusAsync(cancellationToken).ConfigureAwait(false);
        }

        using var response = await SendAsync(() =>
        {
            var content = new StreamContent(block);
            content.Headers.ContentLength = length;
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, fileSize) { Unit = "bytes" };
            content.Headers.TryAddWithoutValidation("Content-Digest", $"sha-256=:{Convert.ToBase64String(sha256)}:");
            return new HttpRequestMessage(HttpMethod.Put, Path($"/files/{Uri.EscapeDataString(fileKey)}")) { Content = content };
        }, BlockRequest, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferImportResult> ImportAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Path("/import")), ShortRequest, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync<ImportBody>(response, cancellationToken).ConfigureAwait(false);
        return new TransferImportResult(body.VmId, body.Name);
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Path("/complete")), ShortRequest, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Path()), ShortRequest, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();

    private string Path(string suffix = "") => $"{ContractInfo.BasePath.TrimStart('/')}/transfers/{request.TransferId:D}{suffix}";

    /// <summary>
    /// Sends to the address that answered last. Before any has, or after a network failure (the client may have moved
    /// to another network), tries each address in turn.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var candidates = _base is { } known ? [known] : request.Target.Addresses.Select(BaseUri).ToList();
        PeerTransportException? last = null;
        foreach (var candidate in candidates)
        {
            using var message = create();
            message.RequestUri = new Uri(candidate, message.RequestUri!);
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(timeout);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
            {
                throw new TransferException($"{request.Target.Name} at {candidate.Host} did not present the certificate it was paired with.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                last = new PeerTransportException($"{request.Target.Name} could not be reached at {candidate.Host}: {ex.Message}", ex);
                _base = null;
                continue;
            }

            _base = candidate;
            await ThrowIfFailedAsync(response, cancellationToken).ConfigureAwait(false);
            return response;
        }

        throw last ?? new PeerTransportException($"{request.Target.Name} has no address to try.");
    }

    private async Task ThrowIfFailedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        using (response)
        {
            Problem? problem = null;
            try
            {
                problem = await response.Content.ReadFromJsonAsync<Problem>(Json, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException or IOException)
            {
                // No problem details; the status code says enough.
            }

            if (problem is { Code: "transferOffset", ExpectedOffset: { } expected })
            {
                throw new TransferOffsetException(expected);
            }

            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
            {
                throw new PeerTransportException($"{request.Target.Name} is busy ({(int)response.StatusCode}).");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new TransferException($"{request.Target.Name} no longer accepts this transfer; its ticket ended. Start the transfer again.");
            }

            throw new TransferException($"{request.Target.Name} refused: {problem?.Detail ?? problem?.Title ?? response.ReasonPhrase ?? response.StatusCode.ToString()}");
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) =>
        await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
            ?? throw new TransferException("The other host sent an empty answer.");

    private Uri BaseUri(string address) => new UriBuilder(Uri.UriSchemeHttps, address.Trim('[', ']'), request.Target.Port, "/").Uri;

    private sealed record StatusBody(IncomingTransferState State, List<FileBody> Files);

    private sealed record FileBody(string Key, long Size, long Received);

    private sealed record ImportBody(Guid VmId, string Name);

    private sealed record Problem(string? Title, string? Detail, string? Code, long? ExpectedOffset);
}

/// <summary>A read-only window on a file, read by position so several windows can share one handle.</summary>
internal sealed class FileSegmentStream(SafeFileHandle handle, long offset, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, length);
    }

    public override int Read(byte[] buffer, int offset1, int count) => Read(buffer.AsSpan(offset1, count));

    public override int Read(Span<byte> buffer)
    {
        var read = RandomAccess.Read(handle, buffer[..(int)Math.Min(buffer.Length, length - _position)], offset + _position);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await RandomAccess.ReadAsync(handle, buffer[..(int)Math.Min(buffer.Length, length - _position)], offset + _position, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset1, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset1, count), cancellationToken).AsTask();

    public override long Seek(long offset1, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset1,
        SeekOrigin.Current => _position + offset1,
        _ => length + offset1,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset1, int count) => throw new NotSupportedException();
}
