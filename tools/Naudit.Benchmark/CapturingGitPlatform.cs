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

    public Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        capture.RecordCheckout();
        return inner.GetCheckoutAsync(request, ct);
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
