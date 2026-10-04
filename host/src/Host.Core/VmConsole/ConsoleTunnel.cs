using System.Buffers;

namespace HyperHarbor.Host.Core.VmConsole;

/// <param name="BytesToVm">Bytes copied from the client to the console service.</param>
/// <param name="BytesFromVm">Bytes copied from the console service to the client.</param>
public sealed record ConsoleTunnelResult(long BytesToVm, long BytesFromVm, TimeSpan Duration);

/// <summary>Copies bytes both ways between a client and the console service until either side closes.</summary>
public static class ConsoleTunnel
{
    private const int BufferSize = 64 * 1024;

    public static async Task<ConsoleTunnelResult> PumpAsync(Stream client, Stream vm, TimeProvider time, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var toVm = CopyAsync(client, vm, stop.Token);
        var fromVm = CopyAsync(vm, client, stop.Token);

        // Remote Desktop has no half-closed state, so the first side to close ends the tunnel.
        await Task.WhenAny(toVm, fromVm).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
        return new ConsoleTunnelResult(await toVm.ConfigureAwait(false), await fromVm.ConfigureAwait(false), time.GetElapsedTime(started));
    }

    /// <summary>Copies until the source ends, the destination fails, or the tunnel stops; returns the bytes copied.</summary>
    private static async Task<long> CopyAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                total += read;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // A closed or reset side ends the tunnel normally.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return total;
    }
}
