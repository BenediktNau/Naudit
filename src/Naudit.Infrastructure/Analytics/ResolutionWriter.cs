using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Analytics;

/// <summary>Schreibt den Resolution-Status eines Findings unter der Präzedenz-Regel (Review-Analytics):
/// explizite Quellen (Command/Checkbox/Emoji/WebUi) überschreiben immer; "Llm" schreibt nur, wenn
/// leer oder selbst zuletzt "Llm"; Undo (status=null) löscht nur, wenn die aktuelle Quelle GENAU die
/// rückgängig gemachte ist. Liefert true, wenn sich etwas geändert hat (redelivery-sichere Bestätigung).</summary>
public static class ResolutionWriter
{
    private const string Llm = "Llm";

    public static async Task<bool> ApplyAsync(
        NauditDbContext db, ReviewFindingEntity finding, string? status, string source, string by, CancellationToken ct = default)
    {
        // Darf diese Quelle den aktuellen Zustand ändern?
        var currentSource = finding.ResolutionSource;
        if (status is null)
        {
            // Undo: nur die eigene Quelle darf löschen; nichts zu tun, wenn schon leer.
            if (finding.ResolutionStatus is null || currentSource != source)
                return false;
        }
        else if (source == Llm)
        {
            // LLM füllt nur Lücken oder korrigiert sich selbst — nie eine explizite Entscheidung.
            if (finding.ResolutionStatus is not null && currentSource != Llm)
                return false;
        }
        // Explizite Quellen überschreiben immer (kein Guard nötig).

        // Keine echte Änderung ⇒ false, ohne SaveChanges.
        if (finding.ResolutionStatus == status
            && (status is null || (finding.ResolutionSource == source)))
            return false;

        finding.ResolutionStatus = status;
        finding.ResolutionSource = status is null ? null : source;
        finding.ResolvedBy = status is null ? null : by;
        finding.ResolvedAtUtc = status is null ? null : DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
