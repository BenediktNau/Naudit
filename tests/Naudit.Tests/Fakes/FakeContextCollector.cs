using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeContextCollector(ReviewContext? context = null, bool throws = false) : IContextCollector
{
    public bool Called { get; private set; }

    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        Called = true;
        if (throws)
            throw new InvalidOperationException("collector boom");
        return Task.FromResult(context ?? ReviewContext.Empty);
    }
}
