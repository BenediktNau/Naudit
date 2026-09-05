using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Verdichtet/normalisiert die aggregierten Funde vor dem Grounding. Default deterministisch;
/// optional später LLM-basiert. Liefert eine <see cref="FindingReduction"/>: die Einzelbefunde für den
/// Prompt und getrennt davon alle Altlasten für den aggregierten Bericht.</summary>
public interface IFindingReducer
{
    Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
