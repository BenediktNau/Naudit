using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Setup;

/// <summary>Persistiert den Wizard-Zwischenstand als EINE Zeile (Id=1): JSON-Blob,
/// Data-Protection-verschlüsselt (er enthält Tokens/Keys, bevor sie echte Settings werden).
/// Nicht entschlüsselbar (Keyring weg) ⇒ null — der Wizard startet dann leer.</summary>
public sealed class SetupDraftService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.SetupDraft";
    private const int DraftId = 1;

    public async Task SaveAsync(string json, CancellationToken ct = default)
    {
        var stored = dataProtection.CreateProtector(ProtectorPurpose).Protect(json);
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null)
            db.SetupDrafts.Add(new SetupDraftEntity { Id = DraftId, Json = stored, UpdatedAtUtc = DateTime.UtcNow });
        else
        {
            row.Json = stored;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> LoadAsync(CancellationToken ct = default)
    {
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null) return null;
        try
        {
            return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(row.Json);
        }
        catch (CryptographicException)
        {
            return null; // Keyring weg ⇒ Draft gilt als nicht vorhanden, kein Crash.
        }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var row = await db.SetupDrafts.SingleOrDefaultAsync(d => d.Id == DraftId, ct);
        if (row is null) return;
        db.SetupDrafts.Remove(row);
        await db.SaveChangesAsync(ct);
    }
}
