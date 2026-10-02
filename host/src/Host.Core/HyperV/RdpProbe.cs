using System.Collections.Concurrent;
using System.Net.Sockets;

namespace HyperHarbor.Host.Core.HyperV;

/// <summary>Checks whether a guest accepts connections on the Remote Desktop port.</summary>
public interface IRdpProbe
{
    Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// TCP connect to port 3389 with a short timeout. Results are cached briefly because the client
/// refreshes the VM list every few seconds.
/// </summary>
public sealed class TcpRdpProbe : IRdpProbe
{
    private const int Port = 3389;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (bool Reachable, DateTimeOffset CheckedAt)> _cache = new();

    public TcpRdpProbe(TimeProvider time)
    {
        _time = time;
    }

    public async Task<bool> IsReachableAsync(string address, CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(address, out var cached) && _time.GetUtcNow() - cached.CheckedAt < CacheLifetime)
        {
            return cached.Reachable;
        }

        var reachable = await ConnectAsync(address, cancellationToken);
        _cache[address] = (reachable, _time.GetUtcNow());
        return reachable;
    }

    private static async Task<bool> ConnectAsync(string address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(address, Port, timeout.Token);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
