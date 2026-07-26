using Microsoft.Extensions.Configuration;

namespace Naudit.Infrastructure.Settings;

/// <summary>Lese-/Schreibhilfen für Katalog-Werte. Skalare sind trivial; Listen liegen in der DB
/// als eine CSV-Zeile und in der Config als indizierte Kind-Keys (…:0, …:1) — genau die zwei
/// Stellen, an denen sich das unterscheidet, stehen hier und sonst nirgends.</summary>
public static class SettingsValues
{
    public static IEnumerable<string> Split(string value)
        => value.Split(',').Select(p => p.Trim()).Where(p => p.Length > 0);

    public static string Normalize(string value) => string.Join(",", Split(value));

    /// <summary>Sichtbarer Wert für die Settings-API. Listen werden als CSV zurückgegeben —
    /// config[key] ist bei Listen IMMER null, der Wert steht in den Kind-Keys.</summary>
    public static string? Read(IConfiguration config, SettingDefinition definition)
    {
        if (!definition.IsList) return config[definition.Key];
        var items = config.GetSection(definition.Key).GetChildren()
            .Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        return items.Count == 0 ? null : string.Join(",", items);
    }

    /// <summary>Ist der Key in DIESER Config-Quelle gesetzt? Für Listen zählt jedes Kind:
    /// Naudit__Sast__Analyzers__0=trivy setzt keinen Elternwert, wäre also sonst unsichtbar —
    /// und die UI würde einen env-gesetzten Key fälschlich als editierbar anbieten.</summary>
    public static bool IsSet(IConfiguration config, SettingDefinition definition)
        => definition.IsList
            ? config.GetSection(definition.Key).GetChildren().Any()
            : config[definition.Key] is not null;
}
