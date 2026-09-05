using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Zähler je Schweregrad, absteigend sortiert.</summary>
public sealed record SeverityCount(FindingSeverity Severity, int Count);

/// <summary>Eine nach Regel verdichtete Gruppe von Altlasten. Beispielort ist deterministisch der
/// kleinste Pfad/die kleinste Zeile der Gruppe.</summary>
public sealed record PreExistingRuleGroup(
    string Rule, FindingCategory Category, FindingSeverity Severity, int Count,
    string? ExampleFilePath, int? ExampleLine);

/// <summary>Deterministische Verdichtung der Funde außerhalb des Diffs.
///
/// Die Verdichtung ist bewusst <b>severity-abhängig</b>: Funde ab
/// <see cref="PreExistingOptions.DetailSeverity"/> bleiben einzeln mit Datei und Zeile stehen,
/// alles darunter wird nach Regel gruppiert. Grund: bei einem realen Repo (cal.com, voller
/// Opengrep-Regelbaum) verteilen sich 2.817 Funde auf 48 Regeln, wobei eine einzige Lint-Regel
/// 62,6 % stellt — uniformes Top-N würde ein echtes Sicherheitsproblem in unberührtem Code hinter
/// i18n-Rauschen verstecken. Die gesamte High-Ebene sind dort 21 Zeilen.</summary>
public sealed record PreExistingSummary(
    int Total,
    IReadOnlyList<SeverityCount> BySeverity,
    IReadOnlyList<ScanFinding> Detailed,
    int DetailedOmitted,
    IReadOnlyList<PreExistingRuleGroup> Groups,
    int GroupedTotal,
    int GroupsOmitted)
{
    public static readonly PreExistingSummary Empty = new(0, [], [], 0, [], 0, 0);

    public bool IsEmpty => Total == 0;

    public static PreExistingSummary Build(IReadOnlyList<ScanFinding> preExisting, PreExistingOptions options)
    {
        if (preExisting.Count == 0)
            return Empty;

        var bySeverity = preExisting
            .GroupBy(f => f.Severity)
            .Select(g => new SeverityCount(g.Key, g.Count()))
            .OrderByDescending(s => s.Severity)
            .ToList();

        // Einzelliste und Rest in EINEM Durchlauf ueber die kanonisch sortierte Menge aufteilen.
        // Bewusst kein Set-Abzug: ScanFinding ist ein record mit Wertgleichheit — zwei gleich
        // befuellte Funde waeren "derselbe" und der Rest damit zu klein. Ueber die Position in
        // der Sortierung geht es exakt und ohne Comparer-Akrobatik. Funde, die der Deckel
        // schluckt, fallen in die Gruppierung: sie duerfen nicht spurlos verschwinden.
        var detailed = new List<ScanFinding>();
        var rest = new List<ScanFinding>();
        var detailedOmitted = 0;
        var maxDetailed = Math.Max(0, options.MaxDetailed);
        foreach (var f in preExisting
                     .OrderByDescending(f => f.Severity)
                     .ThenBy(f => f.FilePath)
                     .ThenBy(f => f.Line)
                     .ThenBy(f => f.RuleId))
        {
            if (f.Severity < options.DetailSeverity)
                rest.Add(f);
            else if (detailed.Count < maxDetailed)
                detailed.Add(f);
            else
            {
                rest.Add(f);
                detailedOmitted++;
            }
        }

        var groupsAll = rest
            .GroupBy(f => (Rule: f.RuleId ?? f.Tool, f.Category))
            .Select(g =>
            {
                var example = g.OrderBy(f => f.FilePath).ThenBy(f => f.Line).First();
                return new PreExistingRuleGroup(g.Key.Rule, g.Key.Category,
                    g.Max(f => f.Severity), g.Count(), example.FilePath, example.Line);
            })
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Rule)
            .ToList();
        var groups = groupsAll.Take(Math.Max(0, options.MaxRules)).ToList();

        return new PreExistingSummary(
            preExisting.Count, bySeverity,
            detailed, detailedOmitted,
            groups, rest.Count, groupsAll.Count - groups.Count);
    }
}
