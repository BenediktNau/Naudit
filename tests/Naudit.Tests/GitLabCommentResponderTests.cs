// tests/Naudit.Tests/GitLabCommentResponderTests.cs
using System.Net;
using System.Text;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitLabCommentResponderTests
{
    private static IGitTokenProvider Tokens() =>
        new ConfiguredGitTokenProvider("tok", System.Array.Empty<ProjectTokenEntry>());

    private static ReviewCommentReply Reply(long? authorId = 42) =>
        new("7", 13, "abc123", "reason", "bob", AuthorAssociation: null, authorId);

    private static GitLabCommentResponder Responder(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new System.Uri("https://gitlab.example.com/") }, Tokens());

    [Theory]
    [InlineData(50, true)]   // Owner
    [InlineData(40, true)]   // Maintainer
    [InlineData(30, true)]   // Developer (Schwelle)
    [InlineData(20, false)]  // Reporter
    [InlineData(10, false)]  // Guest
    public async Task IsAuthorizedAsync_requiresDeveloperOrAbove(int accessLevel, bool expected)
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{ "id": 42, "access_level": {{accessLevel}} }""", Encoding.UTF8, "application/json"),
        });

        Assert.Equal(expected, await Responder(handler).IsAuthorizedAsync(Reply()));
        Assert.Equal("https://gitlab.example.com/api/v4/projects/7/members/all/42", handler.Calls[0].Uri!.ToString());
        Assert.Equal("tok", handler.Requests[0].Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task IsAuthorizedAsync_false_whenNotAMember_404()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.False(await Responder(handler).IsAuthorizedAsync(Reply()));
    }

    [Fact]
    public async Task IsAuthorizedAsync_false_whenAuthorIdMissing()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.False(await Responder(handler).IsAuthorizedAsync(Reply(authorId: null)));
        Assert.Empty(handler.Calls);   // ohne Id kein Lookup
    }

    [Fact]
    public async Task PostReplyAsync_addsNoteToDiscussion_withPrivateToken()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        await Responder(handler).PostReplyAsync(Reply(), "Als False Positive gemerkt.");

        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/7/merge_requests/13/discussions/abc123/notes", call.Uri!.ToString());
        Assert.Contains("Als False Positive gemerkt.", call.Body);
        Assert.Equal("tok", handler.Requests[0].Headers.GetValues("PRIVATE-TOKEN").Single());
    }
}
