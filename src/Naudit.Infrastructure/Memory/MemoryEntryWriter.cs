using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Memory;

/// <summary>Gemeinsamer, idempotenter FP-Upsert aus einem Finding — genutzt von der WebUI (FP-Button)
/// UND vom "@naudit fp"-Antwort-Kommando. Anker ist SourceFindingId (unique unter nicht-null):
/// existiert der Eintrag, wird er reaktiviert/aktualisiert statt dupliziert. Das Doppel-POST-Race auf
/// dem Unique-Index (DbUpdateException) wird idempotent aufgelöst statt in einen 500 zu laufen.</summary>
public static class MemoryEntryWriter
{
    public static async Task<MemoryEntryEntity> MarkFalsePositiveAsync(
        NauditDbContext db, ReviewFindingEntity finding, string? reason, string createdBy, CancellationToken ct = default)
    {
        var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == finding.Id, ct);
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
            entry.Reason = reason.Trim();

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
            entry.Active = true;
            if (!string.IsNullOrWhiteSpace(reason))
                entry.Reason = reason.Trim();
            await db.SaveChangesAsync(ct);
        }
        return entry;
    }
}
