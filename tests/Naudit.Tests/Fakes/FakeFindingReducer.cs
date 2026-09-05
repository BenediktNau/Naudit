using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Identitäts-Reducer: isoliert die ReviewService-Tests von der Verdichtungslogik.
// Selected = alles, PreExisting = alles ohne InDiff — dieselbe Aufteilungsregel wie der echte.
// preExistingOverride ist additiv (Default null = altes Verhalten): erlaubt Tests, dem Service
// eine bewusst kaputte PreExisting-Liste unterzuschieben (z.B. fuer den SafeBuildBaseline-Fail-open-Pfad),
// ohne die vielen bestehenden `new FakeFindingReducer()`-Aufrufe anzufassen.
internal sealed class FakeFindingReducer(IReadOnlyList<ScanFinding>? preExistingOverride = null) : IFindingReducer
{
    public Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult(new FindingReduction(findings, preExistingOverride ?? findings.Where(f => !f.InDiff).ToList()));
}
