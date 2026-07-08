using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;

namespace Naudit.Infrastructure.Setup;

/// <summary>Ergebnis der Pflichtset-Prüfung: fehlt etwas, fährt der Host im Setup-Modus
/// hoch (Wizard statt Webhooks). Wird als Singleton registriert (Status-Endpoint).</summary>
public sealed record SetupStatusResult(bool SetupRequired, IReadOnlyList<string> MissingKeys);

/// <summary>Prüft die effektive Config (DB + env) auf das Pflichtset je Plattform/Provider.
/// Leitprinzip: FEHLENDE Werte ⇒ Setup-Modus; UNGÜLTIGE Werte (un-parsebare Enums) ⇒ keine
/// Aussage hier — die fängt der Probe/Recovery-Modus in Program.cs. Wird auch von
/// POST /api/setup/apply zur Draft-Validierung wiederverwendet (Draft + env als Config).</summary>
public static class SetupStatus
{
    public static SetupStatusResult Check(IConfiguration config)
    {
        var missing = new List<string>();

        // Plattform: leer ⇒ Default GitLab (wie GitOptions); un-parsebar ⇒ keine Plattform-Pflichten.
        var platform = GitPlatformKind.GitLab;
        var platformKnown = TryReadEnum(config["Naudit:Git:Platform"], ref platform);
        if (platformKnown && platform == GitPlatformKind.GitLab)
        {
            Require(config, missing, "Naudit:GitLab:BaseUrl");
            Require(config, missing, "Naudit:GitLab:Token");
            Require(config, missing, "Naudit:GitLab:WebhookSecret");
        }
        else if (platformKnown)
        {
            var auth = GitHubAuthKind.Pat;
            var authKnown = TryReadEnum(config["Naudit:GitHub:Auth"], ref auth);
            if (authKnown && auth == GitHubAuthKind.App)
            {
                Require(config, missing, "Naudit:GitHub:App:AppId");
                Require(config, missing, "Naudit:GitHub:App:PrivateKey");
            }
            else if (authKnown)
            {
                Require(config, missing, "Naudit:GitHub:Token");
            }
            Require(config, missing, "Naudit:GitHub:WebhookSecret");
        }

        // AI: Model ist Pflicht (außer ClaudeCode — CLI defaultet auf "sonnet");
        // ApiKey nur bei Key-Providern. Endpoint hat überall funktionierende Defaults.
        var provider = AiProvider.Ollama;
        var providerKnown = TryReadEnum(config["Naudit:Ai:Provider"], ref provider);
        if (providerKnown)
        {
            if (provider != AiProvider.ClaudeCode)
                Require(config, missing, "Naudit:Ai:Model");
            if (provider is AiProvider.Anthropic or AiProvider.OpenAICompatible)
                Require(config, missing, "Naudit:Ai:ApiKey");
        }

        return new SetupStatusResult(missing.Count > 0, missing);
    }

    /// <summary>Leer ⇒ true (Default bleibt stehen); parsebar ⇒ true + Wert; sonst false.</summary>
    private static bool TryReadEnum<T>(string? raw, ref T value) where T : struct
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(typeof(T), parsed)) { value = parsed; return true; }
        return false;
    }

    private static void Require(IConfiguration config, List<string> missing, string key)
    {
        if (string.IsNullOrWhiteSpace(config[key])) missing.Add(key);
    }
}
