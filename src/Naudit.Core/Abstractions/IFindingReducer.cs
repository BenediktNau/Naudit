using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Verdichtet/normalisiert die aggregierten Funde vor dem Grounding. Default deterministisch;
/// optional später LLM-basiert. Liefert wieder ScanFinding[], damit der Prompt-Aufbau reducer-agnostisch bleibt.</summary>
public interface IFindingReducer
{
    Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
