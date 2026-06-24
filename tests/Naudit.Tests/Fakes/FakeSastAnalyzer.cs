using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeSastAnalyzer(string name, IReadOnlyList<ScanFinding> findings, bool throws = false) : ISastAnalyzer
{
    public string Name => name;

    public Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        if (throws)
            throw new InvalidOperationException("analyzer boom");
        return Task.FromResult(findings);
    }
}
