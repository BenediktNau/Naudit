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
    [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
}

public sealed class GitHubPullRequest
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("user")] public GitHubUser? User { get; set; }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}

public sealed class GitHubFile
{
    [JsonPropertyName("filename")] public string Filename { get; set; } = "";
    [JsonPropertyName("patch")] public string? Patch { get; set; }
}

/// <summary>Antwort von POST …/reviews — die Review-Id, um danach genau die Kommentare DIESES Reviews zu holen.</summary>
public sealed record GitHubReviewResponse([property: JsonPropertyName("id")] long Id);

/// <summary>Ein Review-Comment aus GET …/reviews/{id}/comments — Id + Position, zum Matchen an unsere Inline-Kommentare.</summary>
public sealed record GitHubReviewComment(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("line")] int? Line);
