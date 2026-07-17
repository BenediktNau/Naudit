using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Memory;

/// <summary>No-Op bei Naudit:Review:Memory:Enabled=false — heutiges Verhalten.</summary>
public sealed class NullReviewMemory : IReviewMemory
{
    public Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
}
