using System.IO.Pipes;
using System.Text;
using HyperHarbor.Shared.Contracts.Ipc;

namespace HyperHarbor.Host.Service.Installation;

/// <summary>
/// scheduled-sleep: what the "HyperHarbor\Console sleep" task runs. It asks the running service whether the host may
/// sleep now. Exit code 0 means it is going to sleep, or the service said not to try again; 1 means something is in use
/// (the task scheduler tries again later); 2 means the service did not answer.
/// </summary>
internal static class ScheduledSleepCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static async Task<int> RunAsync()
    {
        using var cancel = new CancellationTokenSource(Timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", TrayPipe.Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(cancel.Token);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(TrayPipe.Serialize(new ScheduledSleepRequestMessage()).AsMemory(), cancel.Token);

            // The service also sends its status messages first; the answer is the one that matters.
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                if (Parse(line) is ScheduledSleepResultMessage result)
                {
                    return result.Accepted || result.Final ? 0 : 1;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException or TimeoutException)
        {
            // The service is not running or did not answer in time.
        }

        return 2;
    }

    private static TrayMessage? Parse(string line)
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
