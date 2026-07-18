// src/Naudit.Infrastructure/Git/ReviewCommentReply.cs
namespace Naudit.Infrastructure.Git;

/// <summary>Plattform-neutrale Antwort auf einen Naudit-Inline-Kommentar mit FP-Kommando.
/// <paramref name="ReplyToCommentId"/> ist die Plattform-Id des URSPRÜNGLICHEN Kommentars
/// (GitHub in_reply_to_id / GitLab discussion_id) und matcht <c>ReviewFindingEntity.PlatformCommentId</c>.
/// Autorisierungs-Signal ist plattform-spezifisch: GitHub liefert <paramref name="AuthorAssociation"/>
/// direkt in der Payload, GitLab braucht <paramref name="AuthorId"/> für einen Mitglieds-Lookup.</summary>
public sealed record ReviewCommentReply(
    string ProjectId,
    int MergeRequestIid,
    string ReplyToCommentId,
    string? Reason,
    string AuthorLogin,
    string? AuthorAssociation,
    long? AuthorId);
