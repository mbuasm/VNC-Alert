using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace MaszynyAlert.Core.Tests.TestUtil;

/// <summary>Jeden zapisany wpis logu (kategoria, poziom, sformatowana treść, ewentualny wyjątek).</summary>
public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Logger zapisujący wszystko do wspólnej listy — pozwala testom sprawdzić, że komunikat pojawił się
/// (albo NIE pojawił się) i ile razy. Bez zależności od konkretnej implementacji logowania.
/// </summary>
public sealed class RecordingLoggerFactory : ILoggerFactory
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    /// <summary>Wszystkie zapisane wpisy, w kolejności zapisania.</summary>
    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    /// <summary>Wpisy zawierające podany fragment tekstu (porównanie zwykłe, wrażliwe na wielkość liter).</summary>
    public IReadOnlyList<LogEntry> Containing(string fragment) =>
        _entries.Where(e => e.Message.Contains(fragment, StringComparison.Ordinal)).ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, _entries);

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class RecordingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _sink;

        public RecordingLogger(string category, ConcurrentQueue<LogEntry> sink)
        {
            _category = category;
            _sink = sink;
        }

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
            ArgumentNullException.ThrowIfNull(formatter);
            _sink.Enqueue(new LogEntry(_category, logLevel, formatter(state, exception), exception));
        }
    }
}
