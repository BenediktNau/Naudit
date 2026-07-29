namespace Naudit.Infrastructure.Setup;

/// <summary>Prüft, ob die Plattform-Seite Naudit die Antworten auf Inline-Kommentare überhaupt
/// zustellt — GitHub die Ereignisart "pull_request_review_comment", GitLab note_events. Fehlt das
/// Abonnement, fallen die "@naudit fp"/"@naudit ok"-Kommandos still aus: kein Fehler, keine
/// Log-Zeile, keine Antwort im Thread.</summary>
public interface ICommentEventProbe
{
    Task<CommentEventStatus> CheckAsync(CancellationToken ct = default);
}

/// <summary>Unknown = nicht ermittelbar (API-Fehler, fehlende Rechte, Gruppen-Hook). Erzeugt
/// bewusst KEINE Warnung: wer sich an Fehlalarme gewöhnt, übersieht den echten Fall.</summary>
public enum CommentEventState { Ok, Missing, Unknown }

/// <summary>Details sind fertige Handlungsanweisungen fürs Log — je betroffenem Ziel eine.
/// Summary ist die neutrale Ein-Zeilen-Zusammenfassung dessen, was der Probe tatsächlich
/// feststellen konnte (auch bei Ok/Unknown) — sie klingt nie nach Alarm, das bleibt den
/// Details/Warnungen vorbehalten.</summary>
public sealed record CommentEventStatus(
    CommentEventState State, IReadOnlyList<string> Details, string Summary = "")
{
    public static readonly CommentEventStatus Ok = new(CommentEventState.Ok, []);
    public static readonly CommentEventStatus Unknown = new(CommentEventState.Unknown, []);
}
