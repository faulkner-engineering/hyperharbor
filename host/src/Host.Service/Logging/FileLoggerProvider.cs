using System.Globalization;
using System.Text;
using System.Threading.Channels;
using HyperHarbor.Host.Core.Security;

namespace HyperHarbor.Host.Service.Logging;

/// <summary>
/// Writes log entries to logs\host-yyyyMMdd.log in the data directory, the same entries the console shows
/// (the Logging section filters both). Files are created with the ProtectedFile ACL and kept for
/// <see cref="RetentionDays"/> days. Writing happens on a background task so logging never blocks a request.
/// Nothing secret reaches the log: SecretLeakTests check every log entry the host writes.
/// </summary>
[ProviderAlias("File")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const string FolderName = "logs";
    public const int RetentionDays = 14;

    private readonly string _folder;
    private readonly TimeProvider _time;
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private readonly System.Collections.Concurrent.ConcurrentQueue<TaskCompletionSource> _flushRequests = new();

    public FileLoggerProvider(string dataDirectory, TimeProvider? time = null)
    {
        _folder = Path.Combine(dataDirectory, FolderName);
        _time = time ?? TimeProvider.System;
        DeleteOldFiles();
        _writer = Task.Run(WriteAsync);
    }

    public string Folder => _folder;

    /// <summary>The file entries are written to today.</summary>
    public string CurrentPath => PathFor(_time.GetLocalNow());

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        _lines.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Logging must never take the service down.
        }
    }

    /// <summary>Waits until everything logged so far is on disk. For tests.</summary>
    internal async Task FlushAsync()
    {
        var done = new TaskCompletionSource();
        _flushRequests.Enqueue(done);
        _lines.Writer.TryWrite(string.Empty);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private void Enqueue(string line) => _lines.Writer.TryWrite(line);

    private string PathFor(DateTimeOffset day) =>
        Path.Combine(_folder, $"host-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    private async Task WriteAsync()
    {
        FileStream? stream = null;
        string? openPath = null;
        try
        {
            while (await _lines.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_lines.Reader.TryRead(out var line))
                {
                    if (line.Length == 0)
                    {
                        continue;
                    }

                    try
                    {
                        var path = CurrentPath;
                        if (path != openPath)
                        {
                            stream?.Dispose();
                            stream = ProtectedFile.OpenAppend(path);
                            openPath = path;
                        }

                        stream!.Write(Encoding.UTF8.GetBytes(line));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // The console still shows the entry; try the file again with the next one.
                        stream?.Dispose();
                        stream = null;
                        openPath = null;
                    }
                }

                stream?.Flush();
                while (_flushRequests.TryDequeue(out var request))
                {
                    request.TrySetResult();
                }
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }

    private void DeleteOldFiles()
    {
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            var cutoff = _time.GetUtcNow().UtcDateTime.AddDays(-RetentionDays);
            foreach (var file in new DirectoryInfo(_folder).EnumerateFiles("host-*.log"))
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    file.Delete();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Old files are only a matter of disk space.
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var line = new StringBuilder()
                .Append(provider._time.GetLocalNow().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(Level(logLevel))
                .Append(' ')
                .Append(category)
                .Append(": ")
                .Append(formatter(state, exception));
            if (exception is not null)
            {
                line.Append(Environment.NewLine).Append(exception);
            }

            provider.Enqueue(line.Append(Environment.NewLine).ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            _ => "CRIT ",
        };
    }
}
