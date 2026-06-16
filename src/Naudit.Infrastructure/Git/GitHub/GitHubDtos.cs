using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Git.GitHub;

public sealed class GitHubWebhookPayload
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; }
    [JsonPropertyName("pull_request")] public GitHubPullRequest? PullRequest { get; set; }
}

public sealed class GitHubRepository
{
    [JsonPropertyName("full_name")] public string? FullName { get; set; }
}

public sealed class GitHubPullRequest
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}

public sealed class GitHubFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("patch")] public string? Patch { get; set; }
}
