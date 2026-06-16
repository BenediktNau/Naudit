using Naudit.Core.Models;

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

        return new ReviewRequest(payload.Project.Id.ToString(), attrs.Iid, attrs.Title ?? "");
    }
}
