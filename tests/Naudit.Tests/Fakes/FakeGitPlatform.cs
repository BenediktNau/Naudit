using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeGitPlatform(IReadOnlyList<CodeChange> changes) : IGitPlatform
{
    public string? PostedMarkdown { get; private set; }
    public IReadOnlyList<InlineComment> PostedComments { get; private set; } = [];
    public int PostCallCount { get; private set; }

    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(changes);

    public Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, CancellationToken ct = default)
    {
        PostedMarkdown = summaryMarkdown;
        PostedComments = comments;
        PostCallCount++;
        return Task.CompletedTask;
    }

    public Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new RepoCheckoutInfo("https://token@host/repo.git", "refs/test/head"));
}
