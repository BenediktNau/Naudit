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

        if (def.IsList)
        {
            // Listen liegen als EINE CSV-Zeile in der DB; leer nach dem Normalisieren heißt
            // "zurück auf Default" — sonst stünde dort eine Zeile mit leerem Wert.
            value = SettingsValues.Normalize(value);
            if (value.Length == 0) { await RemoveAsync(def.Key, ct); return; }
        }

        value = Canonicalize(def, value);

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

    /// <summary>Prüft Werte gegen AllowedValues und schreibt sie in der Schreibweise des Katalogs.
    /// Die Validierung sitzt hier statt nur in der Settings-API, damit die Invariante für JEDEN
    /// Aufrufer gilt (Tests, Seed, künftiges Admin-Tooling) — ein ungültiger Analyzer-Name fiele
    /// sonst erst als Recovery-Modus beim nächsten Start auf. Kanonisiert wird, weil die WebUI
    /// Werte exakt vergleicht: "TRIVY" dürfte nicht als eigener Eintrag neben "trivy" stehen.</summary>
    private static string Canonicalize(SettingDefinition def, string value)
    {
        if (def.AllowedValues is not { } allowed) return value;
        var items = def.IsList ? SettingsValues.Split(value) : [value];
        return string.Join(",", items.Select(item =>
            allowed.FirstOrDefault(a => string.Equals(a, item, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{item}' ist kein gültiger Wert für '{def.Key}'. Erlaubt: {string.Join(", ", allowed)}.")));
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
