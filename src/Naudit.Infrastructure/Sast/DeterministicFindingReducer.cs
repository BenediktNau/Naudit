using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Sast;

/// <summary>Deterministische Verdichtung: Dedup nach (Datei, Zeile, RuleId, Category), dreistufige
/// Sortierung (Diff-Zeile vor geänderter Datei vor unberührter Datei, dann Severity), getrennte
/// Kontingente pro Category. Altlasten belegen dadurch nie Plätze, die Diff-Befunden zustehen.</summary>
public sealed class DeterministicFindingReducer(
    int maxFindingsPerGroup = 30, int maxPreExistingPerGroup = 5) : IFindingReducer
{
    public Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        // Kanonische Sortierung EINMAL: Stufe zuerst, dann Severity, dann stabile Tiebreaker.
        // Dadurch ist sowohl die Kappung als auch die Ausgabe unabhaengig von der Input-Reihenfolge.
        var ordered = findings
            .GroupBy(f => (f.FilePath, f.Line, f.RuleId, f.Category))
            .Select(g => g.First())                       // Erstes Vorkommen in Input gewinnt bei Duplikaten
            .OrderBy(Tier)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        // Getrennte Kontingente je Category: Diff-Befunde schoepfen ihres voll aus, bevor
        // Befunde aus geaenderten Dateien ihr eigenes, kleines Kontingent bekommen.
        // Stufe 2 (unberuehrte Dateien) kommt nie einzeln in den Prompt — sie erscheint
        // ausschliesslich aggregiert im Altlasten-Bericht.
        var selected = ordered
            .GroupBy(f => f.Category)
            .SelectMany(g => g.Where(f => f.InDiff).Take(maxFindingsPerGroup)
                .Concat(g.Where(f => !f.InDiff && f.InChangedFile).Take(maxPreExistingPerGroup)))
            .OrderBy(Tier)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        // Ungekappt: der Bericht zaehlt und gruppiert selbst, er braucht die volle Menge.
        // Eigene Sortierung rein nach Severity (ohne Stufen-Vorrang aus "ordered"): der
        // Altlasten-Bericht kennt keine Diff-Naehe mehr, alle Eintraege hier sind !InDiff.
        var preExisting = ordered
            .Where(f => !f.InDiff)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        return Task.FromResult(new FindingReduction(selected, preExisting));
    }

    // 0 = auf kommentierbarer Diff-Zeile, 1 = in geänderter Datei außerhalb der Hunks,
    // 2 = in unberührter Datei.
    private static int Tier(ScanFinding f) => f.InDiff ? 0 : f.InChangedFile ? 1 : 2;
}
