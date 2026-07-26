namespace Naudit.Infrastructure.Ai.Logging;

/// <summary>Verknüpft alle LLM-Aufrufe eines Reviews mit dem späteren ReviewEntity-Audit.
/// Zur LLM-Aufrufzeit existiert die Review-Zeile noch nicht (der Audit-Sink schreibt sie
/// erst NACH dem Call), darum eine eigene Korrelations-Id statt eines FKs.</summary>
public sealed record ReviewCorrelation(Guid Id, string ProjectId, int PrNumber, string Trigger);

/// <summary>Ambient-Zugriff auf die Korrelation des laufenden Reviews. Bewusst KEIN Core-Seam:
/// Core (ReviewService) bleibt unangetastet — gesetzt wird sie am Review-Eintritt in
/// Infrastructure/Web (ReviewBackgroundService, POST /review), gelesen im PromptLoggingBehavior
/// und im EfReviewAuditSink. AsyncLocal ⇒ fließt durch die await-Kette, auch über DI-Scopes hinweg,
/// und hält parallele Reviews sauber getrennt.</summary>
public interface IReviewCorrelationAccessor
{
    ReviewCorrelation? Current { get; set; }
}

public sealed class AsyncLocalReviewCorrelationAccessor : IReviewCorrelationAccessor
{
    private static readonly AsyncLocal<ReviewCorrelation?> _current = new();

    public ReviewCorrelation? Current
    {
        get => _current.Value;
        set => _current.Value = value;
    }
}
