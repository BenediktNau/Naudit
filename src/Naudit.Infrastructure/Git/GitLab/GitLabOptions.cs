namespace Naudit.Infrastructure.Git.GitLab;

public sealed class GitLabOptions
{
    public string BaseUrl { get; set; } = "";       // z. B. https://gitlab.example.com
    public string Token { get; set; } = "";          // Personal/Project Access Token mit api-Scope, globaler Fallback
    public string WebhookSecret { get; set; } = "";  // Vergleich gegen Header X-Gitlab-Token

    // Per-Projekt-Override: Token je numerischer Projekt-ID; leer ⇒ globaler Token.
    public List<ProjectTokenEntry> ProjectTokens { get; set; } = new();
}
