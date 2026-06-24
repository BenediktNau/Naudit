using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
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
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.OK, json), Options.Create(new GitLabOptions { Token = "tok" }));

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task PostReviewAsync_postsNoteWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Options.Create(new GitLabOptions { Token = "tok" }));

        await platform.PostReviewAsync(Request, "## Naudit Review", []);

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("/merge_requests/42/notes", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }

    [Fact]
    public async Task PostReviewAsync_postsDiscussionWithPosition_perInlineComment()
    {
        var capture = new StubHttpMessageHandler(req =>
        {
            // GET der MR-Details liefert die diff_refs; alle POSTs sind 201.
            if (req.Method == HttpMethod.Get && req.RequestUri!.ToString().EndsWith("/merge_requests/42"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{ "diff_refs": { "base_sha": "b1", "head_sha": "h1", "start_sha": "s1" } }""",
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Options.Create(new GitLabOptions { Token = "tok" }));

        var comments = new[]
        {
            new InlineComment("src/Foo.cs", 5, null, "added-line finding"),
            new InlineComment("src/Bar.cs", 7, 3, "context finding"),
        };

        await platform.PostReviewAsync(Request, "## Naudit Review", comments);

        var discussions = capture.Calls
            .Where(c => c.Method == HttpMethod.Post && c.Uri!.ToString().Contains("/discussions"))
            .ToList();
        Assert.Equal(2, discussions.Count);
        // diff_refs in der Position
        Assert.All(discussions, d => Assert.Contains("\"head_sha\":\"h1\"", d.Body!));
        // old_path ist bei GitLab immer erforderlich – auch für hinzugefügte Zeilen.
        Assert.All(discussions, d => Assert.Contains("\"old_path\":\"src/", d.Body!));
        // hinzugefügte Zeile: new_line ohne old_line
        Assert.Contains(discussions, d => d.Body!.Contains("\"new_line\":5") && !d.Body.Contains("old_line"));
        // Kontextzeile: new_line UND old_line
        Assert.Contains(discussions, d => d.Body!.Contains("\"new_line\":7") && d.Body.Contains("\"old_line\":3"));
        // Summary-Note wurde ebenfalls gepostet
        Assert.Contains(capture.Calls, c => c.Uri!.ToString().Contains("/notes"));
    }

    [Fact]
    public async Task GetCheckoutAsync_buildsCloneUrlWithToken_andMrRef()
    {
        const string json = """{ "http_url_to_repo": "https://gitlab.example.com/group/proj.git" }""";
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.OK, json),
            Options.Create(new GitLabOptions { Token = "tok" }));

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://oauth2:tok@gitlab.example.com/group/proj.git", info.CloneUrl);
        Assert.Equal("refs/merge-requests/42/head", info.HeadRef);
    }
}
