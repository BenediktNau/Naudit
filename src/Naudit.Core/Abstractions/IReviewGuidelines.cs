namespace Naudit.Core.Abstractions;

/// <summary>Liefert das Architektur-Profil (destillierte Projekt-Guidelines) für ein Review.
/// workspaceDir = der geteilte Checkout (null, wenn keiner stattfand) — die Implementierung
/// destilliert daraus bzw. liefert das gespeicherte Profil. Fail-open lebt in der Implementierung.
/// changedFiles = die im Review geänderten Pfade (repo-relativ). Ändert der MR selbst eine
/// Destillat-Quelle (z. B. CLAUDE.md), darf die Implementierung NICHT aus dem PR-Head destillieren —
/// sonst könnte ein MR sein eigenes "autoritatives" Profil (und den Cache) vergiften.</summary>
public interface IReviewGuidelines
{
    Task<string?> GetAsync(string projectId, string? workspaceDir,
        IReadOnlyCollection<string>? changedFiles = null, CancellationToken ct = default);
}
