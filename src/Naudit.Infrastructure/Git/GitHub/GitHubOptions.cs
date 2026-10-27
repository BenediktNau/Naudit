namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com";  // GitHub REST-API
    public string Token { get; set; } = "";                           // PAT mit Repo-Zugriff (Bearer), globaler Fallback
    public string WebhookSecret { get; set; } = "";                   // HMAC-Secret für X-Hub-Signature-256

    // Per-Projekt-Override: fine-grained PAT je "owner/repo"; leer ⇒ globaler Token.
    public List<ProjectTokenEntry> ProjectTokens { get; set; } = new();

    // Auth-Modus: Pat = heutiges Verhalten (statischer Token), App = GitHub-App-Installation-Token (Task 2).
    public GitHubAuthKind Auth { get; set; } = GitHubAuthKind.Pat;
    public GitHubAppOptions App { get; set; } = new();

    // Opt-in: echtes Review-Verdikt posten (event=APPROVE/REQUEST_CHANGES). Default false = heutiges
    // Verhalten (event=COMMENT). Achtung: GitHub lehnt APPROVE/REQUEST_CHANGES vom PR-Autor mit 422 ab
    // — nur mit App-/Service-Account-Identität aktivieren.
    public bool PostVerdict { get; set; }

    /// <summary>Opt-in PAT-Fallback: Auth=App UND ein PAT konfiguriert (globaler Token oder ein gültiger
    /// ProjectTokens-Eintrag). Sonst bleibt Auth=App exakt beim reinen App-Provider. Bewusst Methode statt
    /// Property, damit der Config-Binder sie nicht anfasst. Mit festem App:InstallationId zählt der globale
    /// Token nicht (s. <see cref="GlobalPatFallbackIgnored"/>) — nur explizite ProjectTokens.</summary>
    public bool UsesAppWithPatFallback()
        => Auth == GitHubAuthKind.App
           && ((!string.IsNullOrWhiteSpace(Token) && App.InstallationId is null)
               || ProjectTokens.Any(e => !string.IsNullOrWhiteSpace(e.Project) && !string.IsNullOrWhiteSpace(e.Token)));

    /// <summary>Auth=App mit festem App:InstallationId UND globalem Token: ohne Per-Repo-Lookup ist
    /// "App nicht installiert" nicht erkennbar, der globale PAT-Fallback würde nie greifen — er wird
    /// daher ignoriert (Startup-Warnung). Per-Projekt-ProjectTokens wirken weiter.</summary>
    public bool GlobalPatFallbackIgnored()
        => Auth == GitHubAuthKind.App && App.InstallationId is not null && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>Wählt die <see cref="Git.IGitTokenProvider"/>-Implementierung für GitHub (config-only, s. CLAUDE.md).</summary>
public enum GitHubAuthKind { Pat, App }

/// <summary>Zugangsdaten der GitHub App (Bot-Identität statt PAT). Aus user-secrets/Env/Coolify, nie aus appsettings.json.</summary>
public sealed class GitHubAppOptions
{
    public string AppId { get; set; } = "";       // numerische App-ID (JWT-iss)
    public string PrivateKey { get; set; } = "";  // PEM — oder Base64-codiertes PEM (env-freundlich)
    public long? InstallationId { get; set; }     // optional: fest verdrahtet, spart den Lookup
}
