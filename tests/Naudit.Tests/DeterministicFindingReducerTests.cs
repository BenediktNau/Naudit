using Naudit.Core.Models;
using Naudit.Infrastructure.Sast;
using Xunit;

namespace Naudit.Tests;

public class DeterministicFindingReducerTests
{
    private static ScanFinding Sast(string file, int line, string rule, FindingSeverity sev, bool inDiff = false)
        => new("semgrep", FindingCategory.Sast, sev, "msg", rule, file, line) { InDiff = inDiff };

    [Fact]
    public async Task Reduce_dedupesIdenticalLocationRuleCategory()
    {
        var reducer = new DeterministicFindingReducer();
        var input = new[] { Sast("a.cs", 1, "R1", FindingSeverity.High), Sast("a.cs", 1, "R1", FindingSeverity.High) };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Single(result);
    }

    [Fact]
    public async Task Reduce_sortsBySeverityDesc_thenInDiffFirst()
    {
        var reducer = new DeterministicFindingReducer();
        var low = Sast("a.cs", 1, "R1", FindingSeverity.Low);
        var critNotInDiff = Sast("b.cs", 2, "R2", FindingSeverity.Critical, inDiff: false);
        var critInDiff = Sast("c.cs", 3, "R3", FindingSeverity.Critical, inDiff: true);

        var result = await reducer.ReduceAsync(new[] { low, critNotInDiff, critInDiff }, []);

        Assert.Equal("R3", result[0].RuleId); // Critical + InDiff zuerst
        Assert.Equal("R2", result[1].RuleId); // Critical, nicht InDiff
        Assert.Equal("R1", result[2].RuleId); // Low zuletzt
    }

    [Fact]
    public async Task Reduce_capsPerCategory()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 2);
        var input = new[]
        {
            Sast("a.cs", 1, "R1", FindingSeverity.Critical),
            Sast("b.cs", 2, "R2", FindingSeverity.High),
            Sast("c.cs", 3, "R3", FindingSeverity.Low),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, f => f.RuleId == "R3"); // niedrigste Severity fällt raus
    }

    [Fact]
    public async Task Reduce_isInputOrderIndependent()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 5);

        // Mix of findings from two categories with varied severities
        var findings = new[]
        {
            // Sast findings
            Sast("file1.cs", 10, "S-A1", FindingSeverity.High, inDiff: true),
            Sast("file2.cs", 20, "S-B1", FindingSeverity.Medium, inDiff: false),
            Sast("file3.cs", 30, "S-C1", FindingSeverity.Low, inDiff: true),
            Sast("file4.cs", 40, "S-D1", FindingSeverity.Critical, inDiff: false),
            // Sca findings (different category)
            new ScanFinding("checkmarx", FindingCategory.Sca, FindingSeverity.Critical, "msg", "SCA-X", "dep.json", 1) { InDiff = true },
            new ScanFinding("checkmarx", FindingCategory.Sca, FindingSeverity.High, "msg", "SCA-Y", "dep2.json", 2) { InDiff = false },
        };

        // Call reducer on original order
        var result1 = await reducer.ReduceAsync(findings, []);

        // Shuffle the input
        var shuffled = findings.OrderBy(_ => Random.Shared.Next()).ToList();
        var result2 = await reducer.ReduceAsync(shuffled, []);

        // Extract comparable tuples
        var projection1 = result1.Select(f => (f.Tool, f.Category, f.Severity, f.RuleId, f.FilePath, f.Line)).ToList();
        var projection2 = result2.Select(f => (f.Tool, f.Category, f.Severity, f.RuleId, f.FilePath, f.Line)).ToList();

        // Results must be identical regardless of input order
        Assert.Equal(projection1.Count, projection2.Count);
        for (int i = 0; i < projection1.Count; i++)
        {
            Assert.Equal(projection1[i], projection2[i]);
        }
    }
}
