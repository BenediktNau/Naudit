using Naudit.Core.Abstractions;

namespace Naudit.Tests.Fakes;

internal sealed class FakeRoundtripCounter(int count = 0, bool throws = false) : IReviewRoundtripCounter
{
    public int CallCount { get; private set; }

    public Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default)
    {
        CallCount++;
        if (throws) throw new InvalidOperationException("DB nicht erreichbar");
        return Task.FromResult(count);
    }
}
