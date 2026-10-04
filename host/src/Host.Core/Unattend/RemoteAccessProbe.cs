using System.Net.Sockets;
using System.Text;

namespace HyperHarbor.Host.Core.Unattend;

/// <summary>Whether a guest's Remote Desktop or SSH service actually answers, not only accepts a connection.</summary>
public interface IRemoteAccessProbe
{
    Task<bool> RdpAnswersAsync(string address, CancellationToken cancellationToken);

    Task<bool> SshAnswersAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// Speaks the first message of each protocol. For Remote Desktop it sends an X.224 Connection Request
/// with an RDP negotiation request (TLS or CredSSP) and requires an X.224 Connection Confirm; for SSH it
/// reads the server's "SSH-2.0-" identification line. A port that accepts connections but says nothing,
/// or says something else, does not count, so a VM is not marked ready while its services start.
/// </summary>
public sealed class TcpRemoteAccessProbe : IRemoteAccessProbe
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    /// <summary>TPKT header, X.224 Connection Request, and RDP_NEG_REQ asking for TLS or CredSSP.</summary>
    private static readonly byte[] ConnectionRequest =
        [0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];

    private const byte ConnectionConfirm = 0xD0;

    private readonly int _rdpPort;
    private readonly int _sshPort;
    private readonly TimeSpan _timeout;

    /// <param name="rdpPort">3389 unless a test listens elsewhere; likewise <paramref name="sshPort"/>.</param>
    public TcpRemoteAccessProbe(int rdpPort = 3389, int sshPort = 22, TimeSpan? timeout = null)
    {
        _rdpPort = rdpPort;
        _sshPort = sshPort;
        _timeout = timeout ?? DefaultTimeout;
    }

    public Task<bool> RdpAnswersAsync(string address, CancellationToken cancellationToken) =>
        TryAsync(address, _rdpPort, cancellationToken, async (stream, token) =>
        {
            await stream.WriteAsync(ConnectionRequest, token).ConfigureAwait(false);

            // TPKT (version 3, reserved, length) then the X.224 header: length indicator, then the code.
            var reply = new byte[7];
            await stream.ReadExactlyAsync(reply, token).ConfigureAwait(false);
            return reply[0] == 0x03 && reply[1] == 0x00 && (reply[5] & 0xF0) == ConnectionConfirm;
        });

    public Task<bool> SshAnswersAsync(string address, CancellationToken cancellationToken) =>
        TryAsync(address, _sshPort, cancellationToken, async (stream, token) =>
        {
            // Servers may send other lines before the identification; it must come within 255 bytes.
            var line = new StringBuilder();
            var one = new byte[1];
            while (line.Length < 255)
            {
                if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0)
                {
                    return false;
                }

                if (one[0] == '\n')
                {
                    if (line.ToString().TrimEnd('\r').StartsWith("SSH-2.0-", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    line.Clear();
                    continue;
                }

                line.Append((char)one[0]);
            }

            return false;
        });

    private async Task<bool> TryAsync(string address, int port, CancellationToken cancellationToken, Func<NetworkStream, CancellationToken, Task<bool>> exchange)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
            return await exchange(client.GetStream(), timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or IOException or EndOfStreamException
            || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return false;
        }
    }
}
