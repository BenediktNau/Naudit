using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeGitPlatform(IReadOnlyList<CodeChange> changes) : IGitPlatform
{
    public string? PostedMarkdown { get; private set; }
    public int PostCallCount { get; private set; }

    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(changes);

    public Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        PostedMarkdown = markdown;
        PostCallCount++;
        return Task.CompletedTask;
    }
}
