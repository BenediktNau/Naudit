using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Identitäts-Reducer: isoliert die ReviewService-Tests von der Verdichtungslogik.
// Selected = alles, PreExisting = alles ohne InDiff — dieselbe Aufteilungsregel wie der echte.
internal sealed class FakeFindingReducer : IFindingReducer
{
    public Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult(new FindingReduction(findings, findings.Where(f => !f.InDiff).ToList()));
}
