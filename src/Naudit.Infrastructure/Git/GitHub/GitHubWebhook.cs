using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitHub;

public static class GitHubWebhook
{
    // Reviewbare PR-Actions: neu, wieder geöffnet, neue Commits gepusht.
    private static readonly string[] ReviewableActions = ["opened", "reopened", "synchronize"];

    /// <summary>Mappt ein GitHub-pull_request-Event auf einen ReviewRequest, oder null wenn nichts zu reviewen ist (auch bei null/anderem eventType).</summary>
    public static ReviewRequest? ToReviewRequest(string? eventType, GitHubWebhookPayload payload)
    {
        if (eventType != "pull_request")
            return null;

        if (payload.PullRequest is null || payload.Repository?.FullName is null)
            return null;

        if (payload.Action is null || !ReviewableActions.Contains(payload.Action))
            return null;

        return new ReviewRequest(payload.Repository.FullName, payload.PullRequest.Number, payload.PullRequest.Title ?? "");
    }
}
