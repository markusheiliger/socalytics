using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace SocAlytics.Platform.Integration.Tests.Infrastructure;

public sealed record CapturedLogEntry(
    string Category,
    int EventId,
    LogLevel Level,
    string Message,
    IReadOnlyDictionary<string, string?> State,
    string? Exception);

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<CapturedLogEntry> _entries = new();

    public IReadOnlyList<CapturedLogEntry> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, value) in pairs)
                {
                    values[key] = value?.ToString();
                }
            }

            entries.Enqueue(new CapturedLogEntry(
                category, eventId.Id, logLevel, formatter(state, exception), values, exception?.ToString()));
        }
    }
}
