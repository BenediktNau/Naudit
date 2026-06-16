using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab und GitHub als Implementierungen vorhanden (per Config gewählt).</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);
    Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default);
}