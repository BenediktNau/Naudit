using System.Text.Json;
using Naudit.Infrastructure.Git;
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
    public void ToReviewRequest_ignoresUpdate_withoutNewCommits()
    {
        // "update" feuert auch bei Label-/Beschreibungs-/Assignee-Änderungen — ohne oldrev kein Review.
        var json = """{ "object_kind": "merge_request", "project": { "id": 7 }, "object_attributes": { "iid": 42, "title": "x", "action": "update" } }""";
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;
        Assert.Null(GitLabWebhook.ToReviewRequest(payload));
    }

    [Fact]
    public void ToReviewRequest_mapsUpdate_withNewCommits()
    {
        // oldrev ist nur gesetzt, wenn wirklich Commits gepusht wurden — dann wird reviewt.
        var json = """{ "object_kind": "merge_request", "project": { "id": 7 }, "object_attributes": { "iid": 42, "title": "x", "action": "update", "oldrev": "abc123" } }""";
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;

        var request = GitLabWebhook.ToReviewRequest(payload);

        Assert.NotNull(request);
        Assert.Equal(42, request!.MergeRequestIid);
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

    [Fact]
    public void ToCommentReply_mapsFpReply_onMergeRequestNote()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = "bob" },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes
            {
                Note = "@naudit fp legacy pattern",
                NoteableType = "MergeRequest",
                DiscussionId = "abc123",
            },
        };

        var reply = GitLabWebhook.ToCommentReply(payload);

        Assert.NotNull(reply);
        Assert.Equal("7", reply!.ProjectId);
        Assert.Equal(13, reply.MergeRequestIid);
        Assert.Equal("abc123", reply.ReplyToCommentId);  // discussion_id → matcht PlatformCommentId
        Assert.Equal("legacy pattern", reply.Reason);
        Assert.Equal("bob", reply.AuthorLogin);
        Assert.Null(reply.AuthorAssociation);            // GitLab: keine Association
        Assert.Equal(42, reply.AuthorId);                // GitLab: user.id für den Mitglieds-Lookup
        Assert.Equal(ReviewCommandKind.FalsePositive, reply.Command);
    }

    [Fact]
    public void ToCommentReply_mapsOkReply_onMergeRequestNote()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = "bob" },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes
            {
                Note = "@naudit ok",
                NoteableType = "MergeRequest",
                DiscussionId = "abc123",
            },
        };

        var reply = GitLabWebhook.ToCommentReply(payload);

        Assert.NotNull(reply);
        Assert.Equal(ReviewCommandKind.Accept, reply!.Command);
    }

    [Fact]
    public void ToCommentReply_null_whenNoteableNotMergeRequest()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = "bob" },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes { Note = "@naudit fp", NoteableType = "Issue", DiscussionId = "abc123" },
        };
        Assert.Null(GitLabWebhook.ToCommentReply(payload));
    }

    [Fact]
    public void ToCommentReply_null_whenNotACommand()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = "bob" },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes { Note = "thanks, merging", NoteableType = "MergeRequest", DiscussionId = "abc123" },
        };
        Assert.Null(GitLabWebhook.ToCommentReply(payload));
    }

    [Fact]
    public void ToCommentReply_null_whenAuthorUsernameMissing()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = null },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes { Note = "@naudit fp", NoteableType = "MergeRequest", DiscussionId = "abc123" },
        };
        Assert.Null(GitLabWebhook.ToCommentReply(payload));
    }

    [Fact]
    public void ToCommentReply_null_whenDiscussionIdMissing()
    {
        var payload = new GitLabNoteEvent
        {
            ObjectKind = "note",
            User = new GitLabNoteUser { Id = 42, Username = "bob" },
            Project = new GitLabProject { Id = 7 },
            MergeRequest = new GitLabNoteMergeRequest { Iid = 13 },
            ObjectAttributes = new GitLabNoteAttributes { Note = "@naudit fp", NoteableType = "MergeRequest", DiscussionId = null },
        };
        Assert.Null(GitLabWebhook.ToCommentReply(payload));
    }
}
