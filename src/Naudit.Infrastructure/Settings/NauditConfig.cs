using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Naudit.Infrastructure.Settings;

/// <summary>Konfigurationsquellen OBERHALB der DB-Settings (User-Secrets, Env, CommandLine).
/// Root[key] != null ⇒ der Key ist per Umgebung übersteuert und in der UI gesperrt.</summary>
public sealed record EnvOverrides(IConfiguration Root);

public static class NauditConfig
{
    /// <summary>Fügt die DB-Settings als Memory-Quelle DIREKT NACH den appsettings-JSONs ein —
    /// Ergebnis: appsettings < DB < User-Secrets/Env/CommandLine. Liefert die darüberliegenden
    /// Quellen als eigenen Config-Root zurück (für die "via environment"-Erkennung der Settings-API).
    /// Vorbedingung: MUSS aufgerufen werden, NACHDEM alle Env-Tier-Quellen (User-Secrets, Environment-
    /// Variablen, Command-Line) am Builder hängen — <see cref="EnvOverrides"/> wird zum Aufrufzeitpunkt
    /// als Snapshot der darüberliegenden Quellen gebaut, spätere Quellen würden darin fehlen.</summary>
    public static EnvOverrides InsertDbSettings(IConfigurationBuilder configuration, IDictionary<string, string?> dbSettings)
    {
        // Einfügeposition: hinter der LETZTEN appsettings*-JSON-Quelle. User-Secrets sind zwar auch
        // eine JsonConfigurationSource, aber ihr Pfad heißt "secrets.json" — sie bleiben oberhalb.
        var insertAt = 0;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource json &&
                Path.GetFileName(json.Path ?? "").StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                insertAt = i + 1;
            }
        }
        // Env-Snapshot VOR dem Einfügen bilden (Skip(insertAt) == Skip(insertAt + 1) danach) —
        // er entscheidet gleich mit, welche DB-Listen überhaupt eingefügt werden.
        var overrides = new ConfigurationBuilder();
        foreach (var source in configuration.Sources.Skip(insertAt))
            overrides.Add(source);
        var overrideRoot = overrides.Build();

        var effective = new Dictionary<string, string?>(dbSettings);
        foreach (var definition in SettingsCatalog.All.Where(d => d.IsList))
        {
            // Listen mergen in der Konfiguration INDEXWEISE über Quellen hinweg: env-Index 0 würde
            // den DB-Index 0 überschreiben, DB-Index 1 aber stehen lassen — eine Mischliste, die
            // niemand so konfiguriert hat. Setzt die Umgebung die Liste, fällt die DB-Liste ganz weg.
            if (!SettingsValues.IsSet(overrideRoot, definition)) continue;
            foreach (var key in effective.Keys
                         .Where(k => k.StartsWith($"{definition.Key}:", StringComparison.OrdinalIgnoreCase))
                         .ToList())
                effective.Remove(key);
        }

        configuration.Sources.Insert(insertAt, new MemoryConfigurationSource { InitialData = effective });
        return new EnvOverrides(overrideRoot);
    }
}
