using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Benchmark;

/// <summary>Liest über die echte Plattform, fängt das Posten ab. Der einzige Grund, warum der
/// Benchmark ohne Schreibzugriff auf GitHub auskommt: Naudit sortiert Funde außerhalb des Diffs
/// bereits selbst aus (ReviewService), die aufgefangene Kommentarmenge ist deshalb dieselbe,
/// die auch gepostet würde.</summary>
public sealed class CapturingGitPlatform(IGitPlatform inner, ReviewCapture capture) : IGitPlatform
{
    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => inner.GetChangesAsync(request, ct);

    /// <summary>Erfolg wird erst NACH der Rückkehr vermerkt, ein Fehlschlag getrennt. Zählte man
    /// vorher, wäre ein am Rate-Limit gescheiterter Checkout als erfolgreich diagnostiziert — und
    /// das diff-only-Review ginge unbemerkt in den Import.</summary>
    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        RepoCheckoutInfo info;
        try
        {
            info = await inner.GetCheckoutAsync(request, ct);
        }
        catch
        {
            capture.RecordCheckoutFailed();
            throw;   // Verhalten der echten Plattform bleibt unverändert (ReviewService schluckt oben)
        }
        capture.RecordCheckoutSucceeded(info.HeadRef);
        return info;
    }

    public Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown,
        IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
    {
        capture.Record(request, summaryMarkdown, comments, verdict);
        // Index-gleiche null-Ids: exakt der dokumentierte Best-Effort-Fall der echten Implementierung.
        IReadOnlyList<PostedComment> ids = comments.Select(_ => new PostedComment(null, null)).ToList();
        return Task.FromResult(ids);
    }
}
