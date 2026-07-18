using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab und GitHub als Implementierungen vorhanden (per Config gewählt).</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);

    /// <summary>Postet den Summary-Kommentar und alle Inline-Kommentare an ihre Diff-Positionen.
    /// Das Verdikt stammt aus dem severity-bewussten Gate; ob es als echter Review-Status gepostet
    /// wird, entscheidet die Plattform-Konfiguration (PostVerdict, Default aus).
    /// Liefert je Eingabe-Inline-Kommentar (index-gleich) die Plattform-Ids des erzeugten Kommentars
    /// zurück (für die spätere Antwort-Zuordnung). Erfassung ist best-effort — schlägt sie fehl,
    /// kommen null-Ids, nie eine Exception.</summary>
    Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default);

    /// <summary>Liefert Klon-URL (inkl. Auth) und Head-Ref des MR/PR für den lokalen Checkout.</summary>
    Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Checkout-Koordinaten. CloneUrl enthält das Token — NICHT loggen.</summary>
public sealed record RepoCheckoutInfo(string CloneUrl, string HeadRef);
