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
    }
}
