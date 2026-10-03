using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HyperHarbor.Host.Tests;

/// <summary>Records every log entry, at every level, with its exception, so tests can search them for secrets.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    /// <summary>Fails when any entry contains one of <paramref name="secrets"/>.</summary>
    public void AssertNoneContain(params string[] secrets)
    {
        foreach (var secret in secrets.Where(secret => !string.IsNullOrEmpty(secret)))
        {
            var leak = Entries.FirstOrDefault(entry => entry.Contains(secret, StringComparison.Ordinal));
            Assert.True(leak is null, $"A log entry contains a secret: {leak}");
        }
    }

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Structured values are included too, in case a template leaves a secret out of the message.
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(", ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            entries.Enqueue($"{logLevel} {category}: {formatter(state, exception)} [{values}] {exception}");
        }
    }
}
