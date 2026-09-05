using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Ein aufgefangener Inline-Kommentar. Severity/Confidence als Text, damit die
/// JSON-Datei ohne Kenntnis der Core-Enums lesbar bleibt.</summary>
public sealed record CapturedComment(
    string FilePath, int NewLine, string Body, string Severity, string Confidence);

/// <summary>Ein vollständig aufgefangener Review — das, was sonst an die Plattform ginge.</summary>
/// <param name="Notes">Aufgefangene Altlasten-Notizen (PostNoteAsync statt PostReviewAsync, Task 6)
/// — ohne dieses Feld landeten sie nur in ReviewCapture.Notes (Prozessspeicher) und tauchten im
/// Ausgabe-JSON, über das die manuelle Abnahme läuft, nie auf.</param>
public sealed record CapturedReview(
    string ProjectId, int MergeRequestIid, string Summary, string Verdict,
    IReadOnlyList<CapturedComment> Comments, IReadOnlyList<string> Notes);

/// <summary>Sammelstelle für den Dekorator. Pro Prozess ein Review nach dem anderen —
/// der Runner läuft bewusst seriell, also genügt "der letzte".</summary>
public sealed class ReviewCapture
{
    private CapturedReview? _record;

    /// <summary>Der zuletzt aufgefangene Review, MIT den bis jetzt aufgefangenen Altlasten-Notizen.
    /// Bewusst bei jedem Zugriff neu zusammengesetzt statt einmal in Record() eingefroren: in
    /// ReviewService.ReviewAsync läuft PostNoteAsync (Altlasten-Bericht, Task 6) ERST NACH
    /// PostReviewAsync — wären die Notizen schon beim Record()-Aufruf festgeschrieben, wäre die
    /// Liste hier immer leer. Program.cs liest Last erst, nachdem ReviewAsync vollständig
    /// durchgelaufen ist, also sind zu diesem Zeitpunkt alle Notizen da.</summary>
    public CapturedReview? Last => _record is null ? null : _record with { Notes = [.. Notes] };

    /// <summary>Wie oft GetCheckoutAsync ERFOLGREICH zurückkam. Erst nach der Rückkehr gezählt:
    /// ein Aufruf, der wirft (GitHub-Rate-Limit), ist kein Checkout.</summary>
    public int CheckoutSuccesses { get; private set; }

    /// <summary>Wie oft der Checkout mit einer Ausnahme abbrach. Niemand in der Pipeline loggt das:
    /// GitHubPlatform.GetCheckoutAsync wirft über EnsureSuccessStatusCode, GitWorkspaceProvider loggt
    /// nur seine git-Unterprozesse und ReviewService.GatherGroundingAsync schluckt still. Das Review
    /// läuft dann diff-only weiter und sähe im Ergebnis nur wie ein schwächeres Review aus.</summary>
    public int CheckoutFailures { get; private set; }

    /// <summary>Head-Ref des Checkouts (aus RepoCheckoutInfo). Die Klon-URL bleibt bewusst
    /// ungespeichert — sie trägt das Token.</summary>
    public string? HeadRef { get; private set; }

    /// <summary>Der tatsächlich ausgecheckte Commit (aus .git/HEAD des Workspace). Der Ref allein
    /// ist immer "refs/pull/N/head" und sagt nichts über den Stand; bei noch offenen Pull Requests
    /// wandert der Head weiter, und die 41 Vergleichstools reviewten einen Schnappschuss vom
    /// Januar 2026. Diese SHA hält fest, was Naudit gesehen hat.</summary>
    public string? HeadSha { get; private set; }

    public void RecordHeadSha(string? sha) => HeadSha = sha;

    /// <summary>Wurde ein Checkout überhaupt versucht? 0 heißt: gar nicht erst angefragt — dann lief
    /// das Review ohne Repo-Kontext und ohne Architektur-Profil (Fehlkonfiguration).</summary>
    public bool CheckoutRequested => CheckoutSuccesses + CheckoutFailures > 0;

    public void RecordCheckoutSucceeded(string headRef)
    {
        CheckoutSuccesses++;
        HeadRef = headRef;
    }

    public void RecordCheckoutFailed() => CheckoutFailures++;

    /// <summary>Wurde überhaupt ein Review-Prompt gesehen? false nach einem abgeschlossenen Review
    /// hieße: der LLM-Aufruf lief nicht über den Dekorator (Verdrahtung kaputt).</summary>
    public bool ReviewPromptSeen { get; private set; }

    /// <summary>Trug der Review-Prompt die Repo-Kontext-Sektion? false ⇒ die Kontextsammlung kam
    /// leer zurück (Checkout weg oder Sammler-Fehler — der WorkspaceContextCollector hat nicht
    /// einmal einen Logger, ReviewService.SafeCollectContextAsync schluckt still).</summary>
    public bool ContextInPrompt { get; private set; }

    /// <summary>Trug der Review-Prompt das Architektur-Profil? false ⇒ Destillation ohne Workspace,
    /// ohne Quelldokumente oder komplett gescheitert (DistillingReviewGuidelines ist fail-open).</summary>
    public bool GuidelinesInPrompt { get; private set; }

    /// <summary>Token-Zahlen aus ChatResponse.Usage (der ClaudeCode-Adapter füllt sie). Ein
    /// auffällig kleiner Prompt-Wert verrät einen gekürzten oder degradierten Prompt.</summary>
    public long? InputTokens { get; private set; }

    public long? OutputTokens { get; private set; }

    /// <summary>Wie viele geänderte Dateien der Review sah. GitHubPlatform.GetChangesAsync holt
    /// bewusst nur EINE Seite (per_page=100) — bei 100 ist der Pull Request womöglich still gekürzt
    /// reviewt. Untergrenze: Dateien ohne Patch (binär/zu groß) sind hier schon aussortiert, ein
    /// voller Seiten-Treffer mit Binärdateien liegt darunter.</summary>
    public int ChangedFiles { get; private set; }

    public void RecordChanges(int count) => ChangedFiles = count;

    /// <summary>Aufgefangene Altlasten-Notizen (PostNoteAsync statt PostReviewAsync) — eigene Liste,
    /// weil dieser Kommentar unabhängig von der Review-Summary gepostet wird (Task 6).</summary>
    public List<string> Notes { get; } = [];

    public void RecordNote(string markdown) => Notes.Add(markdown);

    public void RecordReviewPrompt(bool contextInPrompt, bool guidelinesInPrompt, long? inputTokens, long? outputTokens)
    {
        ReviewPromptSeen = true;
        ContextInPrompt = contextInPrompt;
        GuidelinesInPrompt = guidelinesInPrompt;
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public void Record(ReviewRequest request, string summaryMarkdown,
        IReadOnlyList<InlineComment> comments, ReviewVerdict verdict)
        => _record = new CapturedReview(
            request.ProjectId,
            request.MergeRequestIid,
            summaryMarkdown,
            verdict.ToString(),
            comments.Select(c => new CapturedComment(
                c.FilePath, c.NewLine, c.Body, c.Severity.ToString(), c.Confidence.ToString())).ToList(),
            []);   // Platzhalter — die tatsächlichen Notizen liefert Last (siehe dort): zum
                   // Zeitpunkt von Record() lief PostNoteAsync noch nicht.

    public void Reset()
    {
        _record = null;
        CheckoutSuccesses = 0;
        CheckoutFailures = 0;
        HeadRef = null;
        HeadSha = null;
        ReviewPromptSeen = false;
        ContextInPrompt = false;
        GuidelinesInPrompt = false;
        InputTokens = null;
        OutputTokens = null;
        ChangedFiles = 0;
        Notes.Clear();
    }
}
