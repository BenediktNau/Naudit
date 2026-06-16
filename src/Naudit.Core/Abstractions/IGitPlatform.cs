using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab zuerst; GitHub später als zweite Implementierung.</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);
    Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default);
}