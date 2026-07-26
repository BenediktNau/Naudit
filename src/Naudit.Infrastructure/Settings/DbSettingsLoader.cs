using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Settings;

/// <summary>Bootstrap vor dem Host-Bau: SQLite-Verzeichnis anlegen, DB migrieren, Settings
/// lesen und Secrets entschlüsseln. Baut dafür einen eigenen Minimal-ServiceProvider
/// (DbContext + DP mit Keyring in derselben DB) — der Host existiert hier noch nicht.</summary>
public static class DbSettingsLoader
{
    /// <summary>Fester DP-Anwendungsname: Loader und Host müssen denselben verwenden,
    /// sonst sind gegenseitig verschlüsselte Werte nicht lesbar.</summary>
    public const string DataProtectionAppName = "Naudit";

    public static DbSettingsLoadResult Load(DatabaseOptions options)
    {
        EnsureSqliteDirectory(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NauditDbContext>(b => DatabaseOptions.ConfigureDbContext(b, options));
        services.AddDataProtection().PersistKeysToDbContext<NauditDbContext>()
            .SetApplicationName(DataProtectionAppName);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        db.Database.Migrate();

        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(SettingsService.ProtectorPurpose);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var row in db.Settings.AsNoTracking().ToList())
        {
            // Nur Katalog-Keys aus der DB honorieren: die DB ist eine niedrigere Vertrauensstufe als
            // env. Manuell/stale eingefügte Zeilen für env-only Keys (z. B. Naudit:Ui:Admins) oder
            // entfernte Keys werden ignoriert — sonst könnten sie sich unter env in die Config schummeln.
            if (!SettingsCatalog.TryGet(row.Key, out var definition)) continue;
            // IsSecret bleibt das Zeilen-Flag, nicht der Katalog: es sagt, WIE der Wert gespeichert
            // wurde (ver-/entschlüsselt). SettingsService hält es beim Schreiben synchron zum Katalog;
            // ein späterer Katalog-Flip darf eine korrekt gespeicherte Zeile nicht falsch dekodieren.
            if (!row.IsSecret) { Store(definition, row.Value); continue; }
            try { Store(definition, protector.Unprotect(row.Value)); }
            catch (CryptographicException)
            {
                // Keyring weg/DB kopiert: Wert gilt als fehlend, wird neu abgefragt — kein Crash.
                warnings.Add($"Setting '{row.Key}' ist nicht entschlüsselbar und wird ignoriert (Data-Protection-Keyring gewechselt?). Bitte neu setzen.");
            }
        }
        return new DbSettingsLoadResult(settings, warnings);

        void Store(SettingDefinition definition, string value)
        {
            if (!definition.IsList) { settings[definition.Key] = value; return; }
            // Listen: CSV ⇒ indizierte Kind-Keys, sonst bindet List<string> nichts.
            var index = 0;
            foreach (var item in SettingsValues.Split(value))
                settings[$"{definition.Key}:{index++}"] = item;
        }
    }

    /// <summary>SQLite legt Dateien an, aber keine Verzeichnisse — "Data Source=data/naudit.db"
    /// braucht das Verzeichnis vorab. Für Postgres ein No-op.</summary>
    private static void EnsureSqliteDirectory(DatabaseOptions options)
    {
        if (options.Provider != DbProvider.Sqlite) return;
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}

public sealed record DbSettingsLoadResult(Dictionary<string, string?> Settings, List<string> Warnings);
