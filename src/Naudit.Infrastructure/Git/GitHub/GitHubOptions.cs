namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com";  // GitHub REST-API
    public string Token { get; set; } = "";                           // PAT mit Repo-Zugriff (Bearer), globaler Fallback
    public string WebhookSecret { get; set; } = "";                   // HMAC-Secret für X-Hub-Signature-256

    // Per-Projekt-Override: fine-grained PAT je "owner/repo"; leer ⇒ globaler Token.
    public List<ProjectTokenEntry> ProjectTokens { get; set; } = new();
}
