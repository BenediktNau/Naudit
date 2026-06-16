using System.Net;
using System.Text;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubPlatformTests
{
    private static readonly ReviewRequest Request = new("octo/hello-world", 42, "Title");

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
    }

    [Fact]
    public async Task GetChangesAsync_mapsFilesFromApi_andSkipsFilesWithoutPatch()
    {
        const string json = """
        [
          { "filename": "src/Foo.cs", "patch": "@@ +1 @@\n+x" },
          { "filename": "assets/logo.png" }
        ]
        """;
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, json));

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task GetChangesAsync_requestsPullFilesUrl()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "[]", capture));

        await platform.GetChangesAsync(Request);

        Assert.Contains("repos/octo/hello-world/pulls/42/files", capture.LastRequest!.RequestUri!.ToString());
        // per_page=100 ist die bewusste POC-Paginierungsgrenze — explizit verankern.
        Assert.Contains("per_page=100", capture.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task PostSummaryAsync_postsIssueCommentWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostSummaryAsync(Request, "## Naudit Review");

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("repos/octo/hello-world/issues/42/comments", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
}
