using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Guidelines;

/// <summary>No-Op (Feature aus): nie ein Profil — Prompt bleibt byte-identisch zu heute.</summary>
public sealed class NullReviewGuidelines : IReviewGuidelines
{
    public Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
