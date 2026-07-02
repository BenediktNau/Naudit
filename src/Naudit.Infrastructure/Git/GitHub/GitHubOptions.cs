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
