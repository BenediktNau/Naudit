using Microsoft.Extensions.Logging;

namespace Naudit.Tests.Fakes;

// Sammelt formatierte Log-Nachrichten (inkl. Exception-Text), um z. B. Token-Freiheit zu prüfen.
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception) + (exception is null ? "" : " " + exception));
}
