using Naudit.Core.Abstractions;

namespace Naudit.Tests.Fakes;

/// <summary>Liefert ein festes Profil (oder null) und protokolliert die Aufruf-Argumente.</summary>
internal sealed class FakeReviewGuidelines(string? profile) : IReviewGuidelines
{
    public string? LastProjectId { get; private set; }
    public string? LastWorkspaceDir { get; private set; }

    public Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
    {
        LastProjectId = projectId;
        LastWorkspaceDir = workspaceDir;
        return Task.FromResult(profile);
    }
}
