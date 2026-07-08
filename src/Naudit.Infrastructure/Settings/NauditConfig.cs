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
    /// Quellen als eigenen Config-Root zurück (für die "via environment"-Erkennung der Settings-API).</summary>
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
        configuration.Sources.Insert(insertAt, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>(dbSettings),
        });

        var overrides = new ConfigurationBuilder();
        foreach (var source in configuration.Sources.Skip(insertAt + 1))
            overrides.Add(source);
        return new EnvOverrides(overrides.Build());
    }
}
