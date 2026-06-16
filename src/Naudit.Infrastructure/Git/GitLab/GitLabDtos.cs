using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Git.GitLab;

public sealed class GitLabWebhookPayload
{
    [JsonPropertyName("object_kind")] public string? ObjectKind { get; set; }
    [JsonPropertyName("project")] public GitLabProject? Project { get; set; }
    [JsonPropertyName("object_attributes")] public GitLabMergeRequestAttributes? ObjectAttributes { get; set; }
}

public sealed class GitLabProject
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class GitLabMergeRequestAttributes
{
    [JsonPropertyName("iid")] public int Iid { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
}

public sealed class GitLabChangesResponse
{
    [JsonPropertyName("changes")] public List<GitLabChange>? Changes { get; set; }
}

public sealed class GitLabChange
{
    [JsonPropertyName("new_path")] public string NewPath { get; set; } = "";
    [JsonPropertyName("diff")] public string Diff { get; set; } = "";
}
