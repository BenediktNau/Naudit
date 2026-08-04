using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Ai;

namespace Naudit.Infrastructure.Dast;

/// <summary>Auflösung von Naudit:Review:Dast:Ai — der Chat-Client NUR für den Probing-Loop.
///
/// Warum es die Sektion gibt: DastAnalyzer reicht dem Modell die Playwright-Werkzeuge über
/// ChatOptions.Tools (UseFunctionInvocation). Das kann nicht jeder Provider — der
/// ClaudeCode-CLI-Client ignoriert ChatOptions.Tools vollständig (er kennt nur CLI-natives MCP),
/// der Probe-LLM bekäme also keinen Browser und DAST liefe still ins Leere. Mit dieser Sektion
/// zeigt das Probing auf einen werkzeugfähigen Provider, ohne den Provider der eigentlichen
/// Review anzufassen (z. B. Abo-CLI für die Review, API-Key nur fürs Probing).
///
/// Leere Sektion ⇒ exakt der globale Provider, also heutiges Verhalten.</summary>
public static class DastAiOptions
{
    public const string Section = "Naudit:Review:Dast:Ai";
    private const string GlobalSection = "Naudit:Ai";

    /// <summary>Überlagert den globalen Abschnitt feldweise — mit einer Ausnahme: wechselt die
    /// Sektion den Provider, wird NICHTS geerbt. Ein vom Ollama-Provider geerbtes Model
    /// ("qwen3:14b") an einer Anthropic-Endpoint wäre nur ein verwirrender Laufzeitfehler;
    /// bleibt der Provider gleich, ist Erben dagegen genau das Gewollte (etwa: nur ein eigener
    /// ApiKey oder ein größeres Model fürs Probing).</summary>
    public static AiOptions Resolve(IConfiguration configuration)
    {
        var global = configuration.GetSection(GlobalSection).Get<AiOptions>() ?? new AiOptions();
        var section = configuration.GetSection(Section);

        // Unparsbare Werte hier bewusst NICHT abfangen: Bind() wirft dann gleich, und weil der
        // Aufrufer eager auflöst, landet die Fehlkonfiguration im Startup-Probe/Recovery-Mode —
        // wie bei jedem anderen kaputten Naudit:Ai-Wert, statt später Reviews scheitern zu lassen.
        var providerOverride = section["Provider"];
        var switchesProvider = !string.IsNullOrWhiteSpace(providerOverride)
                               && Enum.TryParse<AiProvider>(providerOverride, ignoreCase: true, out var provider)
                               && provider != global.Provider;

        var options = switchesProvider ? new AiOptions() : global;
        section.Bind(options);
        return options;
    }
}
