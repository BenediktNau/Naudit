namespace Naudit.Infrastructure.Git;

/// <summary>Löst den Git-API-Token pro Projekt auf. Reiner Infrastruktur-Belang — Core sieht nie Tokens.
/// Die Default-Implementierung liest die Config; eine spätere Quelle (Vault/Key Vault/DB) ist nur eine
/// zweite Implementierung derselben Naht.</summary>
public interface IGitTokenProvider
{
    /// <summary>Per-Projekt-Override, sonst der globale Default-Token.</summary>
    string ResolveToken(string projectId);
}

/// <summary>Ein Per-Projekt-Token-Eintrag. Bewusst eine Liste (statt Dictionary), damit der Projekt-Key
/// mit Slash (GitHub "owner/repo") nur im <b>Wert</b> steht — so ist die Config auch über Environment-
/// Variablen (Coolify/Docker) setzbar, deren Namen keine Slashes erlauben.</summary>
public sealed class ProjectTokenEntry
{
    public string Project { get; set; } = "";  // GitHub "owner/repo" oder GitLab numerische Projekt-ID
    public string Token { get; set; } = "";
}

/// <summary>Config-basierter Provider: Per-Projekt-Map + globaler Default. Leere/whitespace Einträge
/// werden verworfen (nie ein leerer Auth-Header). Keys case-insensitiv (GitHub-Namen sind es;
/// GitLab-IDs sind rein numerisch, also unkritisch).</summary>
public sealed class ConfiguredGitTokenProvider : IGitTokenProvider
{
    private readonly string _defaultToken;
    private readonly IReadOnlyDictionary<string, string> _projectTokens;

    public ConfiguredGitTokenProvider(string defaultToken, IEnumerable<ProjectTokenEntry> projectTokens)
    {
        _defaultToken = defaultToken;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in projectTokens)
        {
            // Leerer Projekt-Key oder leerer Token ⇒ ignorieren, damit ResolveToken auf den Default fällt.
            if (!string.IsNullOrWhiteSpace(e.Project) && !string.IsNullOrWhiteSpace(e.Token))
                map[e.Project] = e.Token;  // letzter Eintrag gewinnt bei doppeltem Projekt
        }
        _projectTokens = map;
    }

    public string ResolveToken(string projectId)
        => _projectTokens.TryGetValue(projectId, out var t) ? t : _defaultToken;
}
