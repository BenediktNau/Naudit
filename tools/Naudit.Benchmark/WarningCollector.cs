using Microsoft.Extensions.Logging;

namespace Naudit.Benchmark;

/// <summary>Sammelt Warnings/Errors der Review-Pipeline — einer von drei Wegen, Naudits fail-open-
/// Pfade sichtbar zu machen. Er deckt die Stellen ab, die ihre Fehler zwar schlucken, aber loggen:
/// die git-Unterprozesse des GitWorkspaceProvider, die Guidelines-Destillation, das Review-
/// Gedächtnis und die SAST-Analyzer.
///
/// <para>Er deckt bewusst NICHT alles ab, und das ist der Grund für die übrigen Diagnosewerte:
/// GitHubPlatform.GetCheckoutAsync wirft ungeloggt (⇒ CheckoutFailed am IGitPlatform-Dekorator),
/// der WorkspaceContextCollector hat nicht einmal einen Logger (⇒ ContextInPrompt am
/// IChatClient-Dekorator), und die Audit-Senke meldet Fehler überhaupt nicht —
/// ReviewService.RecordAuditAsync schluckt ohne Log, EfReviewAuditSink loggt nur den
/// Erfolgsfall.</para></summary>
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
