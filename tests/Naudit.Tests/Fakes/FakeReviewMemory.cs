using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

/// <summary>Liefert fixe Einträge und protokolliert den Aufruf.</summary>
internal sealed class FakeReviewMemory(params MemoryEntry[] entries) : IReviewMemory
{
    public string? LastProjectId { get; private set; }

    public Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        LastProjectId = projectId;
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }
}
