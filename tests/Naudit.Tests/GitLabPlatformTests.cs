using System.Net;
using System.Text;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitLabPlatformTests
{
    private static readonly ReviewRequest Request = new("7", 42, "Title");

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.com/") };
    }

    [Fact]
    public async Task GetChangesAsync_mapsChangesFromApi()
    {
        const string json = """{ "changes": [ { "new_path": "src/Foo.cs", "diff": "@@ +1 @@\n+x" } ] }""";
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.OK, json));

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task PostSummaryAsync_postsNoteWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostSummaryAsync(Request, "## Naudit Review");

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("/merge_requests/42/notes", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
}
