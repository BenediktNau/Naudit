using Naudit.Core.Models;
using Naudit.Infrastructure.Sast;
using Xunit;

namespace Naudit.Tests;

public class DeterministicFindingReducerTests
{
    private static ScanFinding Sast(string file, int line, string rule, FindingSeverity sev, bool inDiff = false)
        => new("opengrep", FindingCategory.Sast, sev, "msg", rule, file, line) { InDiff = inDiff };

    private static ScanFinding Tiered(string file, int line, string rule, FindingSeverity sev,
        bool inDiff = false, bool inChangedFile = false)
        => new("opengrep", FindingCategory.Sast, sev, "msg", rule, file, line)
        { InDiff = inDiff, InChangedFile = inChangedFile || inDiff };

    [Fact]
    public async Task Reduce_dedupesIdenticalLocationRuleCategory()
    {
        var reducer = new DeterministicFindingReducer();
        // inDiff: true, damit der dedupte Fund ueberhaupt in Selected landet (Stufe 2 waere
        // sonst nie selektiert) — die Eingabedaten wurden an die neue Stufenlogik angepasst,
        // die Dedup-Aussage selbst bleibt unveraendert.
        var input = new[]
        {
            Tiered("a.cs", 1, "R1", FindingSeverity.High, inDiff: true),
            Tiered("a.cs", 1, "R1", FindingSeverity.High, inDiff: true),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Single(result.Selected);
    }

    [Fact]
    public async Task Reduce_dedupPicksHighestSeverity_independentOfInputOrder()
    {
        // Zwei Tools melden denselben Fund (Datei, Zeile, Regel, Kategorie) mit verschiedener
        // Severity. Der Repraesentant darf nicht davon abhaengen, wer zuerst im Input steht —
        // sonst kippen Kontingent-Auswahl und die Severity-Zaehlung des Altlasten-Berichts je
        // nach Analyzer-Reihenfolge.
        var reducer = new DeterministicFindingReducer();
        var low = Tiered("a.cs", 1, "R1", FindingSeverity.Low, inDiff: true) with { Tool = "zeta" };
        var high = Tiered("a.cs", 1, "R1", FindingSeverity.High, inDiff: true) with { Tool = "alpha" };

        var forward = await reducer.ReduceAsync(new[] { low, high }, []);
        var backward = await reducer.ReduceAsync(new[] { high, low }, []);

        var f = Assert.Single(forward.Selected);
        var b = Assert.Single(backward.Selected);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal(f, b);
    }

    [Fact]
    public async Task Reduce_sortsBySeverityDesc_thenInDiffFirst()
    {
        var reducer = new DeterministicFindingReducer();
        // low/critNotInDiff liegen in einer geaenderten Datei (Stufe 1) statt in einer
        // unberuehrten (Stufe 2) — sonst waeren sie nach der neuen Stufenlogik gar nicht in
        // Selected und der Test koennte die Sortierung nicht mehr pruefen.
        var low = Tiered("a.cs", 1, "R1", FindingSeverity.Low, inChangedFile: true);
        var critNotInDiff = Tiered("b.cs", 2, "R2", FindingSeverity.Critical, inChangedFile: true);
        var critInDiff = Tiered("c.cs", 3, "R3", FindingSeverity.Critical, inDiff: true);

        var result = await reducer.ReduceAsync(new[] { low, critNotInDiff, critInDiff }, []);

        Assert.Equal("R3", result.Selected[0].RuleId); // InDiff (Stufe 0) schlaegt Severity
        Assert.Equal("R2", result.Selected[1].RuleId); // Stufe 1, Critical
        Assert.Equal("R1", result.Selected[2].RuleId); // Stufe 1, Low zuletzt
    }

    [Fact]
    public async Task Reduce_capsPerCategory()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 2);
        // inDiff: true, damit alle drei ins Diff-Kontingent (maxFindingsPerGroup) fallen —
        // das ist genau das Kontingent, das dieser Test prueft.
        var input = new[]
        {
            Tiered("a.cs", 1, "R1", FindingSeverity.Critical, inDiff: true),
            Tiered("b.cs", 2, "R2", FindingSeverity.High, inDiff: true),
            Tiered("c.cs", 3, "R3", FindingSeverity.Low, inDiff: true),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Equal(2, result.Selected.Count);
        Assert.DoesNotContain(result.Selected, f => f.RuleId == "R3"); // niedrigste Severity fällt raus
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
        var projection1 = result1.Selected.Select(f => (f.Tool, f.Category, f.Severity, f.RuleId, f.FilePath, f.Line)).ToList();
        var projection2 = result2.Selected.Select(f => (f.Tool, f.Category, f.Severity, f.RuleId, f.FilePath, f.Line)).ToList();

        // Results must be identical regardless of input order
        Assert.Equal(projection1.Count, projection2.Count);
        for (int i = 0; i < projection1.Count; i++)
        {
            Assert.Equal(projection1[i], projection2[i]);
        }
    }

    [Fact]
    public async Task Reduce_keepsAllDiffFindings_evenWhenOutrankedBySeverity()
    {
        // Abnahme-Kriterium: 18 High-Altlasten + 21 Medium-Diff-Befunde
        // => alle 21 Diff-Befunde ueberleben, Altlasten fuellen nur ihr eigenes Kontingent.
        // Diff-Kontingent bewusst 25 (nicht der Produktions-Default 20): geprueft wird, dass
        // ALTLASTEN keine Diff-Plaetze wegnehmen — nicht, wie gross das Diff-Kontingent ist.
        // Mit 20 wuerde der Test am eigenen Deckel scheitern und die falsche Sache messen.
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 25, maxPreExistingPerGroup: 5);
        var input = Enumerable.Range(0, 18)
            .Select(i => Tiered($"alt{i:D2}.cs", i, $"OLD{i:D2}", FindingSeverity.High))
            .Concat(Enumerable.Range(0, 21)
                .Select(i => Tiered($"neu{i:D2}.cs", i, $"NEW{i:D2}", FindingSeverity.Medium, inDiff: true)))
            .ToList();

        var result = await reducer.ReduceAsync(input, []);

        var diffRules = result.Selected.Where(f => f.InDiff).Select(f => f.RuleId).ToList();
        Assert.Equal(21, diffRules.Count);
        Assert.All(Enumerable.Range(0, 21), i => Assert.Contains($"NEW{i:D2}", diffRules));
    }

    [Fact]
    public async Task Reduce_capsPreExistingSeparately_andNeverTakesDiffSlots()
    {
        // Diff-Kontingent 25 aus demselben Grund wie oben: 21 Diff-Befunde muessen alle passen,
        // damit die Aussage ueber das getrennte Altlasten-Kontingent ueberhaupt pruefbar ist.
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 25, maxPreExistingPerGroup: 5);
        var input = Enumerable.Range(0, 18)
            .Select(i => Tiered($"alt{i:D2}.cs", i, $"OLD{i:D2}", FindingSeverity.High, inChangedFile: true))
            .Concat(Enumerable.Range(0, 21)
                .Select(i => Tiered($"neu{i:D2}.cs", i, $"NEW{i:D2}", FindingSeverity.Medium, inDiff: true)))
            .ToList();

        var result = await reducer.ReduceAsync(input, []);

        Assert.Equal(5, result.Selected.Count(f => !f.InDiff));
        Assert.Equal(26, result.Selected.Count);
    }

    [Fact]
    public async Task Reduce_sortsDiffFindingsFirst_thenSeverity()
    {
        var reducer = new DeterministicFindingReducer();
        // inChangedFile: true, damit der Stufe-1-Fund ueberhaupt in Selected landet (Stufe 2
        // waere nie selektiert) — sonst koennte der Test die Sortierung zwischen InDiff und
        // Severity gar nicht pruefen.
        var critPreExisting = Tiered("b.cs", 2, "R-CRIT", FindingSeverity.Critical, inChangedFile: true);
        var lowInDiff = Tiered("c.cs", 3, "R-LOW", FindingSeverity.Low, inDiff: true);

        var result = await reducer.ReduceAsync(new[] { critPreExisting, lowInDiff }, []);

        Assert.Equal("R-LOW", result.Selected[0].RuleId);   // InDiff schlaegt Severity
        Assert.Equal("R-CRIT", result.Selected[1].RuleId);
    }

    [Fact]
    public async Task Reduce_excludesUntouchedFileFindings_fromSelected_butKeepsThemInPreExisting()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 20, maxPreExistingPerGroup: 5);
        var input = new[]
        {
            Tiered("in-hunk.cs", 1, "R-DIFF", FindingSeverity.Low, inDiff: true),
            Tiered("geaendert.cs", 900, "R-FILE", FindingSeverity.Low, inChangedFile: true),
            Tiered("fremd.cs", 5, "R-FAR", FindingSeverity.Critical),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.DoesNotContain(result.Selected, f => f.RuleId == "R-FAR");   // Stufe 2 nie im Prompt
        Assert.Contains(result.Selected, f => f.RuleId == "R-FILE");        // Stufe 1 schon
        Assert.Equal(2, result.PreExisting.Count);                          // Stufe 1 + 2 im Bericht
        Assert.Equal("R-FAR", result.PreExisting[0].RuleId);                // Severity-sortiert
    }
}
