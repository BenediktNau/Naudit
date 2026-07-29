using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Setup;
using Xunit;

namespace Naudit.Tests;

/// <summary>Der Dienst warnt ausschließlich bei nachgewiesenem Missing und überlebt einen
/// werfenden Probe — eine Diagnose darf den Host nie kippen.</summary>
public class CommentEventCheckServiceTests
{
    private sealed class RecordingLogger : ILogger<CommentEventCheckService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeProbe(Func<CommentEventStatus> result) : ICommentEventProbe
    {
        public Task<CommentEventStatus> CheckAsync(CancellationToken ct = default)
            => Task.FromResult(result());
    }

    private static async Task<RecordingLogger> RunAsync(ICommentEventProbe? probe)
    {
        var services = new ServiceCollection();
        if (probe is not null) services.AddScoped<ICommentEventProbe>(_ => probe);
        var logger = new RecordingLogger();
        var service = new CommentEventCheckService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), logger);

        await service.StartAsync(CancellationToken.None);
        // BackgroundService.StartAsync startet ExecuteAsync über Task.Run(action, stoppingToken)
        // und kehrt sofort zurück; StopAsync bricht denselben Token VOR dem Warten ab. Ohne dieses
        // Zwischen-Await gewinnt in der Praxis fast immer der Abbruch das Rennen gegen den
        // Thread-Pool-Start, und Task.Run verwirft den Delegaten dann komplett — ExecuteAsync liefe
        // nie (empirisch >95 % Fehlschlagquote ohne dieses Await). Erst nach Abschluss der
        // Ausführung selbst darf gestoppt werden.
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);
        return logger;
    }

    [Fact]
    public async Task Missing_logsOneWarningPerDetail()
    {
        var logger = await RunAsync(new FakeProbe(() =>
            new CommentEventStatus(CommentEventState.Missing, ["erste Anweisung", "zweite Anweisung"])));

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("erste Anweisung"));
        Assert.Contains(warnings, w => w.Message.Contains("zweite Anweisung"));
    }

    [Fact]
    public async Task Ok_logsNoWarning()
    {
        var logger = await RunAsync(new FakeProbe(() => CommentEventStatus.Ok));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Unknown_logsNoWarning()
    {
        // Kein Fehlalarm: "nicht ermittelbar" ist kein Befund.
        var logger = await RunAsync(new FakeProbe(() => CommentEventStatus.Unknown));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NoProbeRegistered_doesNothing()
    {
        // GitHub im PAT-Modus: kein Probe im Container, der Dienst muss trotzdem sauber laufen.
        var logger = await RunAsync(null);

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public async Task ThrowingProbe_doesNotPropagate()
    {
        var logger = await RunAsync(new FakeProbe(() => throw new InvalidOperationException("boom")));

        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }
}
