namespace Naudit.Infrastructure.Git.GitLab;

public sealed class GitLabOptions
{
    public string BaseUrl { get; set; } = "";       // z. B. https://gitlab.example.com
    public string Token { get; set; } = "";          // Personal/Project Access Token mit api-Scope
    public string WebhookSecret { get; set; } = "";  // Vergleich gegen Header X-Gitlab-Token
}
