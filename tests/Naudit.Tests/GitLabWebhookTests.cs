using System.Text.Json;
using Naudit.Infrastructure.Git.GitLab;
using Xunit;

namespace Naudit.Tests;

public class GitLabWebhookTests
{
    private const string MergeRequestEvent = """
    {
      "object_kind": "merge_request",
      "project": { "id": 7 },
      "object_attributes": { "iid": 42, "title": "Add feature X", "action": "open" }
    }
    """;

    [Fact]
    public void ToReviewRequest_mapsMergeRequestEvent()
    {
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(MergeRequestEvent)!;

        var request = GitLabWebhook.ToReviewRequest(payload);

        Assert.NotNull(request);
        Assert.Equal("7", request!.ProjectId);
        Assert.Equal(42, request.MergeRequestIid);
        Assert.Equal("Add feature X", request.Title);
    }

    [Fact]
    public void ToReviewRequest_ignoresNonMergeRequestEvents()
    {
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>("""{ "object_kind": "push" }""")!;
        Assert.Null(GitLabWebhook.ToReviewRequest(payload));
    }

    [Fact]
    public void ToReviewRequest_ignoresNonReviewableActions()
    {
        var json = """{ "object_kind": "merge_request", "project": { "id": 1 }, "object_attributes": { "iid": 1, "title": "x", "action": "close" } }""";
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;
        Assert.Null(GitLabWebhook.ToReviewRequest(payload));
    }

    [Fact]
    public void ToReviewRequest_leavesAuthorLoginNull()
    {
        var payload = new GitLabWebhookPayload
        {
            ObjectKind = "merge_request",
            Project = new GitLabProject { Id = 42 },
            ObjectAttributes = new GitLabMergeRequestAttributes { Iid = 7, Title = "T", Action = "open" },
        };

        Assert.Null(GitLabWebhook.ToReviewRequest(payload)!.AuthorLogin);
    }
}
