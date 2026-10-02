using System.IO.Pipes;
using System.Text;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Tray;

/// <summary>
/// Keeps a connection to the host service's tray pipe, reconnecting when the service restarts.
/// Events are raised on the synchronization context that created the client (the UI thread).
/// </summary>
internal sealed class TrayPipeClient : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly SynchronizationContext _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private StreamWriter? _writer;
    private NamedPipeClientStream? _pipe;

    public TrayPipeClient()
    {
        _context = SynchronizationContext.Current
            ?? throw new InvalidOperationException("Create the pipe client on the UI thread.");
    }

    public event Action<bool>? ConnectionChanged;

    public event Action<TrayMessage>? MessageReceived;

    public bool IsConnected { get; private set; }

    public void Start() => _ = RunAsync(_stop.Token);

    public async Task SendAsync(TrayMessage message)
    {
        await _writeLock.WaitAsync();
        try
        {
            if (_writer is not null)
            {
                await _writer.WriteLineAsync(TrayPipe.Serialize(message));
            }
        }
        catch (IOException)
        {
            // The read loop notices the broken pipe and reconnects.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The executable of the process serving the pipe. Anything the tray runs elevated is taken from
    /// here rather than from a message, so a process that claimed the pipe name cannot choose it.
    /// </summary>
    public string? ServerExecutablePath()
    {
        if (_pipe is not { IsConnected: true } pipe ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId))
        {
            return null;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(".", TrayPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(stop);

                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
                SetConnected(true);

                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                while (await reader.ReadLineAsync(stop) is { } line)
                {
                    if (TryDeserialize(line) is { } message)
                    {
                        _context.Post(_ => MessageReceived?.Invoke(message), null);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                // The service is not running or the pipe broke; retry below.
            }
            finally
            {
                _writer = null;
                _pipe = null;
                SetConnected(false);
            }

            try
            {
                await Task.Delay(RetryDelay, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        _context.Post(_ => ConnectionChanged?.Invoke(connected), null);
    }

    private static TrayMessage? TryDeserialize(string line)
    {
        try
        {
            return TrayPipe.Deserialize(line);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
