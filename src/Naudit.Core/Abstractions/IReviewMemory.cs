using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Liefert die für dieses Review relevanten Gedächtnis-Einträge (FPs + Konventionen).</summary>
public interface IReviewMemory
{
    // Bekommt bewusst die CodeChanges (nicht nur Pfade), damit ein späterer
    // Embedding-Selector ("RAG light") dieselbe Signatur nutzen kann.
    Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
