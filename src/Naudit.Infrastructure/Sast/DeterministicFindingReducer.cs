using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Sast;

/// <summary>Deterministische Verdichtung: Dedup nach (Datei, Zeile, RuleId, Category),
/// Sortierung Severity↓ dann InDiff zuerst, Cap pro Category. Reproduzierbar, kein Recall-Risiko.</summary>
public sealed class DeterministicFindingReducer(int maxFindingsPerGroup = 20) : IFindingReducer
{
    public Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var reduced = findings
            .GroupBy(f => (f.FilePath, f.Line, f.RuleId, f.Category))
            .Select(g => g.First())                       // Erstes Vorkommen in Input gewinnt bei Duplikaten
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.InDiff)
            .GroupBy(f => f.Category)
            .SelectMany(g => g.Take(maxFindingsPerGroup)) // Cap pro Category
            .OrderByDescending(f => f.Severity)           // Kanonische finale Sortierung unabhängig von Input-Reihenfolge
            .ThenByDescending(f => f.InDiff)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        return Task.FromResult<IReadOnlyList<ScanFinding>>(reduced);
    }
}
