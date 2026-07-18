using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Naudit.Infrastructure.Git;
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

    [Fact]
    public void ToReviewRequest_ignoresLabeledAction()
    {
        // Kommentare sind eigene Event-Typen (issue_comment) und fallen am eventType-Filter raus;
        // Metadaten-Actions wie "labeled" scheitern an der Whitelist. Kein Review ohne neue Commits.
        var json = """{ "action": "labeled", "repository": { "full_name": "o/r" }, "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    [Fact]
    public void ToReviewRequest_returnsNull_whenRepositoryMissing()
    {
        var json = """{ "action": "opened", "pull_request": { "number": 1, "title": "x" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    [Fact]
    public void ToReviewRequest_returnsNull_whenPullRequestMissing()
    {
        var json = """{ "action": "opened", "repository": { "full_name": "o/r" } }""";
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
    }

    [Fact]
    public void ToReviewRequest_mapsAuthorLogin_fromPullRequestUser()
    {
        var payload = new GitHubWebhookPayload
        {
            Action = "opened",
            Repository = new GitHubRepository { FullName = "owner/repo" },
            PullRequest = new GitHubPullRequest { Number = 5, Title = "T", User = new GitHubUser { Login = "Alice" } },
        };

        var request = GitHubWebhook.ToReviewRequest("pull_request", payload);

        Assert.Equal("Alice", request!.AuthorLogin);
    }

    [Fact]
    public void ToReviewRequest_missingUser_leavesAuthorLoginNull()
    {
        var payload = new GitHubWebhookPayload
        {
            Action = "opened",
            Repository = new GitHubRepository { FullName = "owner/repo" },
            PullRequest = new GitHubPullRequest { Number = 5, Title = "T" },
        };

        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload)!.AuthorLogin);
    }

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(body)); // Kleinbuchstaben wie GitHub im X-Hub-Signature-256-Header liefert.
    }

    [Fact]
    public void IsValidSignature_acceptsCorrectSignature()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.True(GitHubWebhook.IsValidSignature(body, "topsecret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsWrongSecret()
    {
        var body = Encoding.UTF8.GetBytes("""{"hello":"world"}""");
        var header = Sign("topsecret", body);

        Assert.False(GitHubWebhook.IsValidSignature(body, "other-secret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsNullHeader()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", null));
    }

    [Fact]
    public void IsValidSignature_rejectsMissingPrefix()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "not-a-signature"));
    }

    [Fact]
    public void IsValidSignature_rejectsMalformedHex()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "topsecret", "sha256=zzzz"));
    }

    [Fact]
    public void IsValidSignature_rejectsTamperedBody()
    {
        var original = Encoding.UTF8.GetBytes("""{"ref":"main"}""");
        var tampered = Encoding.UTF8.GetBytes("""{"ref":"evil"}""");
        var header = Sign("topsecret", original);

        Assert.False(GitHubWebhook.IsValidSignature(tampered, "topsecret", header));
    }

    [Fact]
    public void IsValidSignature_rejectsEmptySecret_failClosed()
    {
        var body = Encoding.UTF8.GetBytes("x");
        Assert.False(GitHubWebhook.IsValidSignature(body, "", Sign("", body)));
    }

    [Fact]
    public void ToCommentReply_mapsFpReply_onReviewComment()
    {
        var payload = new GitHubReviewCommentEvent
        {
            Action = "created",
            Repository = new GitHubRepository { FullName = "acme/widgets" },
            PullRequest = new GitHubPullRequestRef { Number = 7 },
            Comment = new GitHubReviewCommentPayload
            {
                Id = 999,
                InReplyToId = 555,
                Body = "@naudit fp intended",
                User = new GitHubUser { Login = "alice" },
                AuthorAssociation = "MEMBER",
            },
        };

        var reply = GitHubWebhook.ToCommentReply("pull_request_review_comment", payload);

        Assert.NotNull(reply);
        Assert.Equal("acme/widgets", reply!.ProjectId);
        Assert.Equal(7, reply.MergeRequestIid);
        Assert.Equal("555", reply.ReplyToCommentId);   // in_reply_to_id → matcht PlatformCommentId
        Assert.Equal("intended", reply.Reason);
        Assert.Equal("alice", reply.AuthorLogin);
        Assert.Equal("MEMBER", reply.AuthorAssociation);
        Assert.Null(reply.AuthorId);                   // GitHub liefert author_association, keine numerische Id
    }

    [Theory]
    [InlineData("issue_comment")]          // falscher Event-Typ
    [InlineData("pull_request_review_comment")]
    public void ToCommentReply_null_whenNotACommand(string eventType)
    {
        var payload = new GitHubReviewCommentEvent
        {
            Action = "created",
            Repository = new GitHubRepository { FullName = "acme/widgets" },
            PullRequest = new GitHubPullRequestRef { Number = 7 },
            Comment = new GitHubReviewCommentPayload { InReplyToId = 555, Body = "looks fine", AuthorAssociation = "MEMBER" },
        };
        Assert.Null(GitHubWebhook.ToCommentReply(eventType, payload));
    }

    [Fact]
    public void ToCommentReply_null_whenTopLevelComment_noInReplyTo()
    {
        var payload = new GitHubReviewCommentEvent
        {
            Action = "created",
            Repository = new GitHubRepository { FullName = "acme/widgets" },
            PullRequest = new GitHubPullRequestRef { Number = 7 },
            Comment = new GitHubReviewCommentPayload { InReplyToId = null, Body = "@naudit fp", AuthorAssociation = "MEMBER" },
        };
        Assert.Null(GitHubWebhook.ToCommentReply("pull_request_review_comment", payload));
    }

    [Fact]
    public void ToCommentReply_null_whenAuthorLoginMissing()
    {
        var payload = new GitHubReviewCommentEvent
        {
            Action = "created",
            Repository = new GitHubRepository { FullName = "acme/widgets" },
            PullRequest = new GitHubPullRequestRef { Number = 7 },
            Comment = new GitHubReviewCommentPayload
            {
                Id = 999,
                InReplyToId = 555,
                Body = "@naudit fp",
                User = new GitHubUser { Login = null },
                AuthorAssociation = "MEMBER",
            },
        };
        Assert.Null(GitHubWebhook.ToCommentReply("pull_request_review_comment", payload));
    }

    [Fact]
    public void ToCommentReply_null_whenActionNotCreated()
    {
        var payload = new GitHubReviewCommentEvent
        {
            Action = "edited",
            Repository = new GitHubRepository { FullName = "acme/widgets" },
            PullRequest = new GitHubPullRequestRef { Number = 7 },
            Comment = new GitHubReviewCommentPayload { InReplyToId = 555, Body = "@naudit fp", AuthorAssociation = "MEMBER" },
        };
        Assert.Null(GitHubWebhook.ToCommentReply("pull_request_review_comment", payload));
    }
}
