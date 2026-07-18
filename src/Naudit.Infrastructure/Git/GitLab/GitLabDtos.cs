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
    [JsonPropertyName("http_url_to_repo")] public string? HttpUrlToRepo { get; set; }
}

public sealed class GitLabMergeRequestAttributes
{
    [JsonPropertyName("iid")] public int Iid { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("oldrev")] public string? OldRev { get; set; }
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

public sealed class GitLabMergeRequestDetail
{
    [JsonPropertyName("diff_refs")] public GitLabDiffRefs? DiffRefs { get; set; }
    [JsonPropertyName("author")] public GitLabUser? Author { get; set; }
}

public sealed class GitLabUser
{
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class GitLabDiffRefs
{
    [JsonPropertyName("base_sha")] public string BaseSha { get; set; } = "";
    [JsonPropertyName("head_sha")] public string HeadSha { get; set; } = "";
    [JsonPropertyName("start_sha")] public string StartSha { get; set; } = "";
}

public sealed class GitLabApprovals
{
    // Hat der aufrufende User (= Naudits Token-Identität) den MR bereits approved?
    [JsonPropertyName("user_has_approved")] public bool UserHasApproved { get; set; }
}

/// <summary>Antwort von POST …/discussions — Discussion-Id + Wurzel-Note-Id (für die Antwort-Zuordnung).</summary>
public sealed record GitLabDiscussionResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("notes")] List<GitLabDiscussionNote>? Notes);

public sealed record GitLabDiscussionNote([property: JsonPropertyName("id")] long Id);

/// <summary>Payload eines GitLab Note-Hooks (object_kind: note) — die Antwort/der Kommentar.</summary>
public sealed class GitLabNoteEvent
{
    [JsonPropertyName("object_kind")] public string? ObjectKind { get; set; }
    [JsonPropertyName("user")] public GitLabNoteUser? User { get; set; }
    [JsonPropertyName("project")] public GitLabProject? Project { get; set; }
    [JsonPropertyName("object_attributes")] public GitLabNoteAttributes? ObjectAttributes { get; set; }
    [JsonPropertyName("merge_request")] public GitLabNoteMergeRequest? MergeRequest { get; set; }
}

public sealed class GitLabNoteUser
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

public sealed class GitLabNoteAttributes
{
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("noteable_type")] public string? NoteableType { get; set; }
    // Discussion-Id der Antwort — identisch zur Discussion, in der Naudits Finding-Kommentar steckt.
    [JsonPropertyName("discussion_id")] public string? DiscussionId { get; set; }
}

public sealed class GitLabNoteMergeRequest
{
    [JsonPropertyName("iid")] public int Iid { get; set; }
}
