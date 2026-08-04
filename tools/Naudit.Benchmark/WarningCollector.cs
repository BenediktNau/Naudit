using Microsoft.Extensions.Logging;

namespace Naudit.Benchmark;

/// <summary>Sammelt Warnings/Errors der Review-Pipeline. Das ist der belastbare Weg, Naudits
/// fail-open-Pfade sichtbar zu machen: GitWorkspaceProvider, Guidelines-Destillation und die
/// Analyzer schlucken ihre Fehler, loggen sie aber. Ohne diesen Sammler liefe ein Review ohne
/// Repo-Kontext stumm durch und sähe im Ergebnis nur wie ein schwächeres Review aus.</summary>
public sealed class WarningCollector
{
    private readonly List<string> _messages = [];
    private readonly Lock _gate = new();

    public void Add(string message)
    {
        lock (_gate) _messages.Add(message);
    }

    /// <summary>Liefert das Gesammelte und leert den Puffer — einmal pro Review aufgerufen.</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            var copy = _messages.ToArray();
            _messages.Clear();
            return copy;
        }
    }
}

public sealed class CollectingLoggerProvider(WarningCollector collector) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CollectingLogger(categoryName, collector);
    public void Dispose() { }

    private sealed class CollectingLogger(string category, WarningCollector collector) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            collector.Add($"{logLevel}: {category}: {text}");
        }
    }
}
