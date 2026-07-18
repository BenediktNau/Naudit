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

/// <summary>Payload von pull_request_review_comment — die Antwort auf einen Review-Kommentar.</summary>
public sealed class GitHubReviewCommentEvent
{
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("comment")] public GitHubReviewCommentPayload? Comment { get; set; }
    [JsonPropertyName("pull_request")] public GitHubPullRequestRef? PullRequest { get; set; }
    [JsonPropertyName("repository")] public GitHubRepository? Repository { get; set; }
}

public sealed class GitHubReviewCommentPayload
{
    [JsonPropertyName("id")] public long Id { get; set; }
    // Nur Antworten tragen in_reply_to_id; es zeigt auf den Wurzel-Kommentar des Threads (= unsere Finding-Id).
    [JsonPropertyName("in_reply_to_id")] public long? InReplyToId { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("user")] public GitHubUser? User { get; set; }
    [JsonPropertyName("author_association")] public string? AuthorAssociation { get; set; }
}

public sealed class GitHubPullRequestRef
{
    [JsonPropertyName("number")] public int Number { get; set; }
}
