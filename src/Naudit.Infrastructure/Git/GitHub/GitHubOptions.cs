namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com";  // GitHub REST-API
    public string Token { get; set; } = "";                           // PAT mit Repo-Zugriff (Bearer)
    public string WebhookSecret { get; set; } = "";                   // HMAC-Secret für X-Hub-Signature-256
}
