namespace Naudit.Core.Abstractions;

/// <summary>Liefert das Architektur-Profil (destillierte Projekt-Guidelines) für ein Review.
/// workspaceDir = der geteilte Checkout (null, wenn keiner stattfand) — die Implementierung
/// destilliert daraus bzw. liefert das gespeicherte Profil. Fail-open lebt in der Implementierung.</summary>
public interface IReviewGuidelines
{
    Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default);
}
