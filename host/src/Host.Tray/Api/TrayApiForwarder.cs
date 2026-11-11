using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using HyperHarbor.Shared.Contracts;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray.Api;

/// <summary>What the page gets back for an API request.</summary>
/// <param name="Status">The HTTP status; 0 when the request was not sent or no answer came.</param>
/// <param name="Body">The response body (JSON), if any.</param>
/// <param name="Error">Why there is no answer, when Status is 0.</param>
public sealed record TrayApiResponse(int Status, string? Body, string? Error)
{
    public static TrayApiResponse Failed(string error) => new(0, null, error);
}

/// <summary>
/// Sends the tray window's API requests to its own host, as the tray's paired device (docs/host-vm-management.md).
/// The page cannot reach the network itself (its CSP allows no connections), so each request comes over the WebView2
/// bridge and goes out here: only to the routes in <see cref="TrayApiRoutes"/>, with the tray's client certificate,
/// to a host whose certificate matches the fingerprint the service gave on registration.
/// The elevation token stays here, as it stays in the client's Rust side: the page learns only when it expires.
/// </summary>
public sealed class TrayApiForwarder : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private readonly TrayApiIdentity _identity;
    private readonly Func<TrayApiRegisterMessage, Task<TrayApiRegisteredMessage>> _register;
    private readonly SemaphoreSlim _registration = new(1, 1);
    private readonly object _gate = new();
    private Endpoint? _endpoint;
    private (string Token, DateTimeOffset ExpiresAt)? _elevation;

    /// <param name="register">Sends the registration over the tray pipe and returns the service's answer.</param>
    public TrayApiForwarder(TrayApiIdentity identity, Func<TrayApiRegisterMessage, Task<TrayApiRegisteredMessage>> register)
    {
        _identity = identity;
        _register = register;
    }

    /// <param name="path">Relative to /api/v1, for example "/vms".</param>
    /// <param name="body">The JSON request body, if any.</param>
    public async Task<TrayApiResponse> SendAsync(string method, string path, string? body, CancellationToken cancellationToken = default)
    {
        if (!TrayApiRoutes.IsAllowed(method, path))
        {
            return TrayApiResponse.Failed($"The tray does not send {method} {path} to the host.");
        }

        try
        {
            var endpoint = await EndpointAsync(cancellationToken);
            using var response = await SendOnceAsync(endpoint, method, path, body, cancellationToken);

            // The device was removed from the Devices page: register again, once, and repeat the request.
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                Forget(endpoint);
                endpoint = await EndpointAsync(cancellationToken);
                using var retried = await SendOnceAsync(endpoint, method, path, body, cancellationToken);
                return await ReadAsync(retried, method, path, cancellationToken);
            }

            return await ReadAsync(response, method, path, cancellationToken);
        }
        catch (TrayApiUnavailableException ex)
        {
            return TrayApiResponse.Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return TrayApiResponse.Failed($"The host's API did not answer: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TrayApiResponse.Failed("The host's API did not answer in time.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _endpoint?.Client.Dispose();
            _endpoint = null;
        }

        _registration.Dispose();
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Endpoint endpoint, string method, string path, string? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), endpoint.BaseAddress + path.TrimStart('/'));
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (CurrentElevation() is { } token)
        {
            request.Headers.Add(ContractInfo.ElevationHeader, token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await endpoint.Client.SendAsync(request, cancellationToken);
    }

    private async Task<TrayApiResponse> ReadAsync(HttpResponseMessage response, string method, string path, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var status = (int)response.StatusCode;

        if (method == "POST" && path == TrayApiRoutes.ElevationPath && response.IsSuccessStatusCode
            && JsonNode.Parse(text) is JsonObject grant
            && grant["token"]?.GetValue<string>() is { Length: > 0 } token
            && grant["expiresAt"]?.GetValue<string>() is { } expiresAt
            && DateTimeOffset.TryParse(expiresAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expires))
        {
            lock (_gate)
            {
                _elevation = (token, expires);
            }

            return new TrayApiResponse(status, new JsonObject { ["expiresAt"] = expiresAt }.ToJsonString(), null);
        }

        return new TrayApiResponse(status, text.Length == 0 ? null : text, null);
    }

    private string? CurrentElevation()
    {
        lock (_gate)
        {
            return _elevation is { } elevation && elevation.ExpiresAt > DateTimeOffset.UtcNow ? elevation.Token : null;
        }
    }

    private async Task<Endpoint> EndpointAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_endpoint is { } known)
            {
                return known;
            }
        }

        await _registration.WaitAsync(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_endpoint is { } known)
                {
                    return known;
                }
            }

            var answer = await _register(new TrayApiRegisterMessage(_identity.Fingerprint, _identity.DeviceId));
            if (answer is not { DeviceId: { } deviceId, Address: { } address, HostCertificateFingerprint: { } hostFingerprint })
            {
                throw new TrayApiUnavailableException(answer.Problem ?? "The host did not add the tray as a device.");
            }

            _identity.DeviceId = deviceId;
            var endpoint = new Endpoint(
                new Uri($"https://{(IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address)}:{answer.Port}/api/v1/"),
                CreateClient(hostFingerprint));
            lock (_gate)
            {
                _endpoint = endpoint;
            }

            return endpoint;
        }
        finally
        {
            _registration.Release();
        }
    }

    private void Forget(Endpoint endpoint)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_endpoint, endpoint))
            {
                _endpoint = null;

                // A token belongs to a device; the new registration may be a new device.
                _elevation = null;
            }
        }

        endpoint.Client.Dispose();
    }

    private HttpClient CreateClient(string hostFingerprint)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = false,
            SslOptions =
            {
                ClientCertificates = [_identity.Certificate],

                // The host's certificate is self-signed; the fingerprint from the service is the only trust.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    certificate is not null
                    && string.Equals(
                        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                        hostFingerprint,
                        StringComparison.OrdinalIgnoreCase),
            },
        };
        return new HttpClient(handler) { Timeout = RequestTimeout };
    }

    private sealed record Endpoint(Uri BaseAddress, HttpClient Client);

    private sealed class TrayApiUnavailableException(string message) : Exception(message);
}
