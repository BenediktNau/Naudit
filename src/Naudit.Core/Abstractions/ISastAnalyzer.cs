using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Pluggbarer Code-Scanner (SAST/SCA). Mehrere Implementierungen registrierbar.
/// Nicht anwendbar (kein passendes Projekt etc.) ⇒ leere Liste, kein Fehler.</summary>
public interface ISastAnalyzer
{
    string Name { get; }
    Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
