using System.Net;
using System.Net.Sockets;
using HyperHarbor.Host.Core.Unattend;

namespace HyperHarbor.Host.Tests.Unattend;

/// <summary>The probe against real loopback listeners that behave like the services, or not.</summary>
public sealed class RemoteAccessProbeTests : IDisposable
{
    /// <summary>TPKT and X.224 Connection Confirm, then RDP_NEG_RSP selecting CredSSP.</summary>
    private static readonly byte[] ConnectionConfirm =
        [0x03, 0x00, 0x00, 0x13, 0x0E, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00, 0x02, 0x00, 0x08, 0x00, 0x02, 0x00, 0x00, 0x00];

    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _stop = new();

    public void Dispose()
    {
        _stop.Cancel();
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        _stop.Dispose();
    }

    [Fact]
    public async Task Rdp_AnswersOnlyWithAConnectionConfirm()
    {
        var confirming = Serve(async stream =>
        {
            await stream.ReadExactlyAsync(new byte[19]);
            await stream.WriteAsync(ConnectionConfirm);
        });
        var silent = Serve(async stream => await Task.Delay(Timeout.Infinite, _stop.Token));
        var garbage = Serve(async stream => await stream.WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray()));

        Assert.True(await Probe(rdp: confirming).RdpAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.False(await Probe(rdp: silent).RdpAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.False(await Probe(rdp: garbage).RdpAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.False(await Probe(rdp: ClosedPort()).RdpAnswersAsync("127.0.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task Ssh_AnswersWithAnIdentificationLine_EvenAfterOtherLines()
    {
        var ssh = Serve(async stream => await stream.WriteAsync("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13.5\r\n"u8.ToArray()));
        var preamble = Serve(async stream => await stream.WriteAsync("Welcome\r\nSSH-2.0-OpenSSH_9.6\r\n"u8.ToArray()));
        var silent = Serve(async stream => await Task.Delay(Timeout.Infinite, _stop.Token));

        Assert.True(await Probe(ssh: ssh).SshAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.True(await Probe(ssh: preamble).SshAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.False(await Probe(ssh: silent).SshAnswersAsync("127.0.0.1", CancellationToken.None));
    }

    [Fact]
    public async Task EachProtocol_RefusesTheOthersGreeting()
    {
        var ssh = Serve(async stream => await stream.WriteAsync("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray()));
        var rdp = Serve(async stream =>
        {
            await stream.ReadExactlyAsync(new byte[19]);
            await stream.WriteAsync(ConnectionConfirm);
        });

        Assert.False(await Probe(rdp: ssh).RdpAnswersAsync("127.0.0.1", CancellationToken.None));
        Assert.False(await Probe(ssh: rdp).SshAnswersAsync("127.0.0.1", CancellationToken.None));
    }

    private static TcpRemoteAccessProbe Probe(int rdp = 1, int ssh = 1) => new(rdp, ssh, TimeSpan.FromMilliseconds(500));

    /// <summary>Listens on a free loopback port and runs <paramref name="behave"/> for each connection.</summary>
    private int Serve(Func<NetworkStream, Task> behave)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            await behave(client.GetStream());
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException)
                        {
                        }
                    }
                });
            }
        });
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static int ClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
