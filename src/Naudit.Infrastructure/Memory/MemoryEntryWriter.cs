using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Memory;

/// <summary>Gemeinsamer, idempotenter FP-Upsert aus einem Finding — genutzt von der WebUI (FP-Button)
/// UND vom "@naudit fp"-Antwort-Kommando. Anker ist SourceFindingId (unique unter nicht-null):
/// existiert der Eintrag, wird er reaktiviert/aktualisiert statt dupliziert. Das Doppel-POST-Race auf
/// dem Unique-Index (DbUpdateException) wird idempotent aufgelöst statt in einen 500 zu laufen.</summary>
public static class MemoryEntryWriter
{
    /// <summary>Obergrenze für Reason: die WebUI lehnt Längeres mit 400 ab, aber das
    /// "@naudit fp"-Kommando hat keinen synchronen 400-Kanal (der Webhook antwortet immer 200)
    /// — dort wird deshalb gekappt statt abgelehnt. Beide Schreibpfade teilen dieselbe Grenze.</summary>
    public const int MaxReasonLength = 4000;

    private static string Cap(string s)
    {
        if (s.Length <= MaxReasonLength)
            return s;
        var end = MaxReasonLength;
        // Kein Surrogat-Paar (z. B. Emoji) zerschneiden — sonst bleibt ein ungültiges lone surrogate stehen.
        if (char.IsHighSurrogate(s[end - 1]))
            end--;
        return s[..end];
    }

    public static async Task<FpMarkResult> MarkFalsePositiveAsync(
        NauditDbContext db, ReviewFindingEntity finding, string? reason, string createdBy, CancellationToken ct = default)
    {
        var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == finding.Id, ct);
        var newlyMarked = entry is null || !entry.Active;
        if (entry is null)
        {
            entry = new MemoryEntryEntity
            {
                ProjectId = finding.Review.ProjectId,
                Kind = "FalsePositive",
                File = finding.File,
                Text = finding.Text,
                SourceFindingId = finding.Id,
                CreatedBy = createdBy,
                CreatedAt = DateTime.UtcNow,
                Active = true,
            };
            db.MemoryEntries.Add(entry);
        }
        entry.Active = true;
        if (!string.IsNullOrWhiteSpace(reason))
            entry.Reason = Cap(reason.Trim());

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (entry.Id == 0)
        {
            // Race mit parallelem Markieren: beide sahen entry==null, der andere legte zuerst an —
            // der Unique-Index lässt unser Insert scheitern. Idempotent behandeln.
            db.ChangeTracker.Clear();
            entry = await db.MemoryEntries.SingleAsync(m => m.SourceFindingId == finding.Id, ct);
            newlyMarked = !entry.Active;
            entry.Active = true;
            if (!string.IsNullOrWhiteSpace(reason))
                entry.Reason = Cap(reason.Trim());
            await db.SaveChangesAsync(ct);
        }
        return new FpMarkResult(entry, newlyMarked);
    }
}

/// <summary>Ergebnis eines FP-Markier-Versuchs: der (angelegte oder wiederverwendete) Eintrag, und ob
/// DIESER Aufruf ihn von inaktiv/nicht-existent zu aktiv überführt hat (Anker für Duplicate-Reply-
/// Unterdrückung im "@naudit fp"-Kommando — die WebUI ignoriert das Flag).</summary>
public readonly record struct FpMarkResult(MemoryEntryEntity Entry, bool NewlyMarked);
