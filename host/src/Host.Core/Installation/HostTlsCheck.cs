using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace HyperHarbor.Host.Core.Installation;

/// <summary>
/// Connects to the API port as a client would and checks that the host serves its own certificate. The
/// certificate is self-signed, so it is pinned by hash, the way clients pin it. Used by the self-test and by
/// the update helper's health check.
/// </summary>
public static class HostTlsCheck
{
    /// <returns>The negotiated protocol.</returns>
    /// <exception cref="InvalidOperationException">The host served another certificate.</exception>
    public static async Task<SslProtocols> HandshakeAsync(IPAddress address, int port, X509Certificate2 expected, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
        await using var tls = new SslStream(client.GetStream());
        try
        {
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                        certificate is not null && certificate.GetCertHashString() == expected.GetCertHashString(),
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthenticationException ex)
        {
            throw new InvalidOperationException("The host did not serve its own certificate.", ex);
        }

        return tls.SslProtocol;
    }
}
