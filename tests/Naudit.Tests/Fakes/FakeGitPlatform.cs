using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeGitPlatform(IReadOnlyList<CodeChange> changes) : IGitPlatform
{
    public string? PostedMarkdown { get; private set; }
    public IReadOnlyList<InlineComment> PostedComments { get; private set; } = [];
    public ReviewVerdict? PostedVerdict { get; private set; }
    public int PostCallCount { get; private set; }
    public IReadOnlyList<PostedComment> PostedIds { get; set; } = [];

    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(changes);

    public Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
    {
        PostedMarkdown = summaryMarkdown;
        PostedComments = comments;
        PostedVerdict = verdict;
        PostCallCount++;
        // Standard: je Kommentar ein leerer PostedComment, sofern der Test nichts vorgibt.
        IReadOnlyList<PostedComment> result = PostedIds.Count > 0
            ? PostedIds
            : comments.Select(_ => new PostedComment(null, null)).ToList();
        return Task.FromResult(result);
    }

    public Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new RepoCheckoutInfo("https://token@host/repo.git", "refs/test/head"));
}
