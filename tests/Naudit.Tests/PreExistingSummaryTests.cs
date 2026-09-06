using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PreExistingSummaryTests
{
    private static ScanFinding F(FindingSeverity sev, string rule, string file, int line,
        FindingCategory cat = FindingCategory.Sast)
        => new("opengrep", cat, sev, "msg", rule, file, line);

    [Fact]
    public void Build_listsHighAndCritical_individually_withFileAndLine()
    {
        // Der Fall, der die Verdichtung rechtfertigt: ein echtes Sicherheitsproblem ausserhalb
        // geaenderter Dateien darf NICHT im Regel-Aggregat verschwinden.
        var findings = new[]
        {
            F(FindingSeverity.High, "detected-google-oauth-access-token", "apps/web/calendso.yaml", 402),
            F(FindingSeverity.Medium, "i18next-key-format", "a.tsx", 1),
        };

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        var detailed = Assert.Single(summary.Detailed);
        Assert.Equal("detected-google-oauth-access-token", detailed.RuleId);
        Assert.Equal("apps/web/calendso.yaml", detailed.FilePath);
        Assert.Equal(402, detailed.Line);
        Assert.DoesNotContain(summary.Groups, g => g.Severity >= FindingSeverity.High);
    }

    [Fact]
    public void Build_groupsLowerSeverities_byRule_withCountAndExample()
    {
        var findings = Enumerable.Range(1, 1763)
            .Select(i => F(FindingSeverity.Medium, "i18next-key-format", $"f{i}.tsx", i))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        var group = Assert.Single(summary.Groups);
        Assert.Equal("i18next-key-format", group.Rule);
        Assert.Equal(1763, group.Count);
        Assert.Equal("f1.tsx", group.ExampleFilePath);   // niedrigster Pfad = deterministisch
        Assert.Equal(1, group.ExampleLine);
        Assert.Equal(1763, summary.Total);
    }

    [Fact]
    public void Build_countsEverySeverity_evenWhenCapped()
    {
        var findings = Enumerable.Range(0, 21).Select(i => F(FindingSeverity.High, $"H{i}", $"h{i}.cs", i))
            .Concat(Enumerable.Range(0, 2677).Select(i => F(FindingSeverity.Medium, "M", $"m{i}.cs", i)))
            .Concat(Enumerable.Range(0, 119).Select(i => F(FindingSeverity.Info, "I", $"i{i}.cs", i)))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        Assert.Equal(2817, summary.Total);
        Assert.Equal(21, summary.BySeverity.Single(s => s.Severity == FindingSeverity.High).Count);
        Assert.Equal(2677, summary.BySeverity.Single(s => s.Severity == FindingSeverity.Medium).Count);
        Assert.Equal(119, summary.BySeverity.Single(s => s.Severity == FindingSeverity.Info).Count);
        Assert.Equal(21, summary.Detailed.Count);        // Deckel 50 greift nicht
        Assert.Equal(0, summary.DetailedOmitted);
    }

    [Fact]
    public void Build_capsDetailedList_andReportsRemainderAsOmitted()
    {
        var findings = Enumerable.Range(0, 60)
            .Select(i => F(FindingSeverity.Critical, $"C{i:D2}", $"c{i:D2}.cs", i)).ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions { MaxDetailed = 50 });

        Assert.Equal(50, summary.Detailed.Count);
        Assert.Equal(10, summary.DetailedOmitted);
    }

    [Fact]
    public void Build_capsRuleGroups_andReportsOmittedRuleCount()
    {
        var findings = Enumerable.Range(0, 48)
            .SelectMany(r => Enumerable.Range(0, 48 - r)
                .Select(i => F(FindingSeverity.Medium, $"R{r:D2}", $"r{r:D2}-{i}.cs", i)))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions { MaxRules = 30 });

        Assert.Equal(30, summary.Groups.Count);
        Assert.Equal(18, summary.GroupsOmitted);
        Assert.Equal(findings.Count, summary.GroupedTotal);   // Zaehler bleibt vollstaendig
        Assert.Equal("R00", summary.Groups[0].Rule);          // haeufigste Regel zuerst
    }

    [Fact]
    public void Build_onEmptyInput_returnsEmpty()
    {
        var summary = PreExistingSummary.Build([], new PreExistingOptions());

        Assert.True(summary.IsEmpty);
        Assert.Equal(0, summary.Total);
    }

    [Fact]
    public void Build_usesRuleIdFallback_whenFindingHasNoRule()
    {
        var findings = new[] { new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Medium, "msg") };

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        Assert.Equal("trivy", Assert.Single(summary.Groups).Rule);
    }

    [Fact]
    public void Build_isOrderIndependent_whenFindingsDifferOnlyByCategory()
    {
        // Fix-Runde 1: Severity/FilePath/Line/RuleId (Einzelliste) bzw. Rule/Severity/Count
        // (Gruppen) bilden ohne Category-Tiebreak KEINE Totalordnung. Der Reducer dedupliziert
        // auf (FilePath, Line, RuleId, Category) — zwei Funde, die sich NUR in Category
        // unterscheiden, ueberleben die Deduplizierung also und muessten trotzdem deterministisch
        // sortiert werden. Realer Ausloeser fuer die Gruppen-Seite: Trivy liefert ueber mehrere
        // Scan-Typen (Vuln/Secret/Misconfig) denselben Tool-Namen, aber verschiedene Category.
        var detailA = F(FindingSeverity.Critical, "R-DUP", "a.cs", 1);           // -> Detailed (Default DetailSeverity=High)
        var detailB = detailA with { Category = FindingCategory.Sca };
        var groupA = new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Medium, "msg");     // -> Gruppe (RuleId-Fallback auf Tool)
        var groupB = groupA with { Category = FindingCategory.Secrets };

        var forward = new[] { detailA, detailB, groupA, groupB };
        var backward = new[] { groupB, groupA, detailB, detailA };

        var summaryForward = PreExistingSummary.Build(forward, new PreExistingOptions());
        var summaryBackward = PreExistingSummary.Build(backward, new PreExistingOptions());

        Assert.Equal(2, summaryForward.Detailed.Count);
        Assert.Equal(2, summaryForward.Groups.Count);
        Assert.Equal(summaryForward.Detailed, summaryBackward.Detailed);
        Assert.Equal(summaryForward.Groups, summaryBackward.Groups);
    }

    [Fact]
    public void NewKeys_areDbManaged()
    {
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Sast:MaxPreExistingPerGroup", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:Enabled", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:DetailSeverity", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:MaxDetailed", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:MaxRules", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:FirstReviewOnly", out _));
    }
}
