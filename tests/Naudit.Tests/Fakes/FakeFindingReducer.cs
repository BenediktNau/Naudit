using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Identitäts-Reducer: isoliert die ReviewService-Tests von der Verdichtungslogik.
internal sealed class FakeFindingReducer : IFindingReducer
{
    public Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult(findings);
}
