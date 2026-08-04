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

    /// <summary>Wie oft GetCheckoutAsync angefragt wurde. 0 heißt: der Checkout wurde gar nicht
    /// erst versucht — dann lief das Review ohne Repo-Kontext und ohne Architektur-Profil.</summary>
    public int CheckoutCalls { get; private set; }

    public void RecordCheckout() => CheckoutCalls++;

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
        CheckoutCalls = 0;
    }
}
