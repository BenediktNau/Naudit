using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Settings;

/// <summary>Schreibt/löscht DB-verwaltete Settings. Secrets werden mit Data Protection
/// verschlüsselt (Purpose unten) — der DbSettingsLoader entschlüsselt sie beim Bootstrap.</summary>
public sealed class SettingsService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.Settings";

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (!SettingsCatalog.TryGet(key, out var def))
            throw new InvalidOperationException($"'{key}' ist kein verwalteter Setting-Key.");

        var stored = def.IsSecret
            ? dataProtection.CreateProtector(ProtectorPurpose).Protect(value)
            : value;

        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == def.Key, ct);
        if (row is null)
            db.Settings.Add(new SettingEntity { Key = def.Key, Value = stored, IsSecret = def.IsSecret, UpdatedAtUtc = DateTime.UtcNow });
        else
        {
            row.Value = stored;
            row.IsSecret = def.IsSecret;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        // Über den Katalog normalisieren (kanonische Schreibweise) — sonst schlägt ein Remove
        // mit abweichender Groß-/Kleinschreibung unter SQLites BINARY-Collation still fehl.
        if (!SettingsCatalog.TryGet(key, out var def)) return false;
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == def.Key, ct);
        if (row is null) return false;
        db.Settings.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<HashSet<string>> GetSetKeysAsync(CancellationToken ct = default) =>
        new(await db.Settings.Select(s => s.Key).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
}
