// tests/Naudit.Tests/GitHubCommentResponderTests.cs
using System.Net;
using System.Text;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubCommentResponderTests
{
    private static IGitTokenProvider Tokens() =>
        new ConfiguredGitTokenProvider("tok", System.Array.Empty<ProjectTokenEntry>());

    private static ReviewCommentReply Reply(string? association) =>
        new("acme/widgets", 7, "555", "reason", "alice", association, AuthorId: null, Command: ReviewCommandKind.FalsePositive);

    [Theory]
    [InlineData("OWNER", true)]
    [InlineData("MEMBER", true)]
    [InlineData("COLLABORATOR", true)]
    [InlineData("member", true)]            // case-insensitiv
    [InlineData("CONTRIBUTOR", false)]
    [InlineData("NONE", false)]
    [InlineData("FIRST_TIME_CONTRIBUTOR", false)]
    [InlineData(null, false)]
    public async Task IsAuthorizedAsync_gatesOnAuthorAssociation(string? association, bool expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var responder = new GitHubCommentResponder(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.github.com/") }, Tokens());

        Assert.Equal(expected, await responder.IsAuthorizedAsync(Reply(association)));
        Assert.Empty(handler.Calls);   // reine Payload-Prüfung, kein API-Call
    }

    [Fact]
    public async Task PostReplyAsync_postsToRepliesEndpoint_withBearer()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        var responder = new GitHubCommentResponder(
            new HttpClient(handler) { BaseAddress = new System.Uri("https://api.github.com/") }, Tokens());

        await responder.PostReplyAsync(Reply("MEMBER"), "Als False Positive gemerkt.");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.github.com/repos/acme/widgets/pulls/7/comments/555/replies", call.Uri!.ToString());
        Assert.Contains("Als False Positive gemerkt.", call.Body);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok", handler.Requests[0].Headers.Authorization!.Parameter);
    }
}
