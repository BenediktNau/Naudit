using Naudit.Core.Models;
using Naudit.Infrastructure.Git;

namespace Naudit.Infrastructure.Git.GitLab;

public static class GitLabWebhook
{
    private static readonly string[] ReviewableActions = ["open", "reopen", "update"];

    /// <summary>Mappt ein GitLab-Webhook-Payload auf einen ReviewRequest, oder null wenn nichts zu reviewen ist.</summary>
    public static ReviewRequest? ToReviewRequest(GitLabWebhookPayload payload)
    {
        if (payload.ObjectKind != "merge_request")
            return null;

        var attrs = payload.ObjectAttributes;
        if (attrs is null || payload.Project is null)
            return null;

        if (attrs.Action is null || !ReviewableActions.Contains(attrs.Action))
            return null;

        // "update" feuert auch bei Label-/Beschreibungs-/Assignee-Änderungen. Reviewt wird nur,
        // wenn wirklich Commits gepusht wurden — GitLab setzt oldrev genau dann.
        if (attrs.Action == "update" && attrs.OldRev is null)
            return null;

        return new ReviewRequest(payload.Project.Id.ToString(), attrs.Iid, attrs.Title ?? "");
    }

    /// <summary>Mappt ein GitLab-Note-Event auf ein Kommando-Reply, oder null wenn es keine
    /// MergeRequest-Antwort mit gültigem Kommando-Body und Discussion-Id ist — erkannt werden sowohl
    /// "@naudit fp" als auch die Annahme-Verben "@naudit ok"/"angenommen"/"accepted".</summary>
    public static ReviewCommentReply? ToCommentReply(GitLabNoteEvent payload)
    {
        if (payload.ObjectKind != "note")
            return null;
        var attrs = payload.ObjectAttributes;
        if (attrs?.NoteableType != "MergeRequest")
            return null;
        if (string.IsNullOrEmpty(attrs.DiscussionId))
            return null;
        if (payload.MergeRequest is not { } mr)
            return null;
        if (payload.Project is not { } project)
            return null;
        if (payload.User is not { } user)
            return null;

        var cmd = FpReplyCommand.TryParse(attrs.Note);
        if (cmd is null)
            return null;

        // Ohne Autor-Username ist die Antwort nicht zuordenbar (Autorisierung/Protokoll brauchen ihn) —
        // nicht mit einer leeren Identität weiterlaufen.
        if (string.IsNullOrEmpty(user.Username))
            return null;

        return new ReviewCommentReply(project.Id.ToString(), mr.Iid, attrs.DiscussionId, cmd.Reason,
            user.Username, AuthorAssociation: null, user.Id, Command: cmd.Kind);
    }
}
