using System.Text.Json;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubWebhookTests
{
    private const string PullRequestEvent = """
    {
      "action": "opened",
      "repository": { "full_name": "octo/hello-world" },
      "pull_request": { "number": 42, "title": "Add feature X" }
    }
    """;

    [Fact]
    public void ToReviewRequest_mapsPullRequestEvent()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;

        var request = GitHubWebhook.ToReviewRequest("pull_request", payload);

        Assert.NotNull(request);
        Assert.Equal("octo/hello-world", request!.ProjectId);
        Assert.Equal(42, request.MergeRequestIid);
        Assert.Equal("Add feature X", request.Title);
    }

    [Fact]
    public void ToReviewRequest_ignoresNonPullRequestEvents()
    {
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(PullRequestEvent)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("push", payload));
    }

    [Fact]
    public void ToReviewRequest_ignoresNonReviewableActions()
    {
        var json = """{ "action": "closed", "repository": { "full_name": "o/r" }, "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }
}
