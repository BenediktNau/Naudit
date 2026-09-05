namespace Naudit.Core.Models;

/// <summary>Ergebnis der Verdichtung. <paramref name="Selected"/> geht als Einzelbefunde in den
/// Prompt; <paramref name="PreExisting"/> traegt ALLE Funde ausserhalb des Diffs (ungekappt) fuer
/// den aggregierten Altlasten-Bericht. Beide Listen sind bereits dedupliziert.</summary>
/// <param name="Selected">Funde auf kommentierbaren Diff-Zeilen plus ein kleines Kontingent aus
/// geaenderten Dateien; Funde aus unberuehrten Dateien sind hier nie enthalten.</param>
/// <param name="PreExisting">Alle Funde mit <c>InDiff == false</c>, Severity-absteigend.</param>
public sealed record FindingReduction(
    IReadOnlyList<ScanFinding> Selected,
    IReadOnlyList<ScanFinding> PreExisting)
{
    public static readonly FindingReduction Empty = new([], []);
}
