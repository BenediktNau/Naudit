using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab und GitHub als Implementierungen vorhanden (per Config gewählt).</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);

    /// <summary>Postet den Summary-Kommentar und alle Inline-Kommentare an ihre Diff-Positionen.</summary>
    Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default);

    /// <summary>Liefert Klon-URL (inkl. Auth) und Head-Ref des MR/PR für den lokalen Checkout.</summary>
    Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Checkout-Koordinaten. CloneUrl enthält das Token — NICHT loggen.</summary>
public sealed record RepoCheckoutInfo(string CloneUrl, string HeadRef);
