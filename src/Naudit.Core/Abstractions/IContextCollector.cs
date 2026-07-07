using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Schneidet Review-Kontext (Umgebung, Call-Sites, Repo-Überblick) aus dem ausgecheckten Quellbaum.</summary>
public interface IContextCollector
{
    Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
