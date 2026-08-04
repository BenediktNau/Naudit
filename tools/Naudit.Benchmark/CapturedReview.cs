using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Ein aufgefangener Inline-Kommentar. Severity/Confidence als Text, damit die
/// JSON-Datei ohne Kenntnis der Core-Enums lesbar bleibt.</summary>
public sealed record CapturedComment(
    string FilePath, int NewLine, string Body, string Severity, string Confidence);

/// <summary>Ein vollständig aufgefangener Review — das, was sonst an die Plattform ginge.</summary>
public sealed record CapturedReview(
    string ProjectId, int MergeRequestIid, string Summary, string Verdict,
    IReadOnlyList<CapturedComment> Comments);

/// <summary>Sammelstelle für den Dekorator. Pro Prozess ein Review nach dem anderen —
/// der Runner läuft bewusst seriell, also genügt "der letzte".</summary>
public sealed class ReviewCapture
{
    public CapturedReview? Last { get; private set; }

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
        => Last = new CapturedReview(
            request.ProjectId,
            request.MergeRequestIid,
            summaryMarkdown,
            verdict.ToString(),
            comments.Select(c => new CapturedComment(
                c.FilePath, c.NewLine, c.Body, c.Severity.ToString(), c.Confidence.ToString())).ToList());

    public void Reset()
    {
        Last = null;
        CheckoutSuccesses = 0;
        CheckoutFailures = 0;
        HeadRef = null;
        ReviewPromptSeen = false;
        ContextInPrompt = false;
        GuidelinesInPrompt = false;
        InputTokens = null;
        OutputTokens = null;
        ChangedFiles = 0;
    }
}
