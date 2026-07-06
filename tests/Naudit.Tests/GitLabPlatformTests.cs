using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitLabPlatformTests
{
    private static readonly ReviewRequest Request = new("7", 42, "Title");

    // Token-Provider für die Tests: Default-Token, optional Per-Projekt-Overrides (Projekt-ID → Token).
    private static IGitTokenProvider Tokens(string @default = "tok", Dictionary<string, string>? map = null)
        => new ConfiguredGitTokenProvider(@default,
            (map ?? new()).Select(kv => new ProjectTokenEntry { Project = kv.Key, Token = kv.Value }));

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.com/") };
    }

    // Options-Helper: Default = PostVerdict false (heutiges Verhalten).
    private static IOptions<GitLabOptions> Opts(bool postVerdict = false)
        => Options.Create(new GitLabOptions { PostVerdict = postVerdict });

    private static HttpResponseMessage Ok() => new(HttpStatusCode.Created);

    [Fact]
    public async Task GetChangesAsync_mapsChangesFromApi()
    {
        const string json = """{ "changes": [ { "new_path": "src/Foo.cs", "diff": "@@ +1 @@\n+x" } ] }""";
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.OK, json), Tokens(), Opts());

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task GetChangesAsync_usesPerProjectToken_inPrivateTokenHeader()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"changes":[]}""", Encoding.UTF8, "application/json"),
        });
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.OK, """{"changes":[]}""", capture),
            Tokens("default-tok", new() { ["7"] = "proj-tok" }),
            Opts());

        await platform.GetChangesAsync(Request);  // Request.ProjectId == "7"

        Assert.Equal("proj-tok", capture.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task GetChangesAsync_unmappedProject_fallsBackToDefaultToken()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"changes":[]}""", Encoding.UTF8, "application/json"),
        });
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.OK, """{"changes":[]}""", capture),
            Tokens("default-tok", new() { ["999"] = "proj-tok" }),
            Opts());

        await platform.GetChangesAsync(Request);

        Assert.Equal("default-tok", capture.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
    }

    [Fact]
    public async Task PostReviewAsync_postsNoteWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(), Opts());

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

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
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(), Opts());

        var comments = new[]
        {
            new InlineComment("src/Foo.cs", 5, null, "added-line finding"),
            new InlineComment("src/Bar.cs", 7, 3, "context finding"),
        };

        await platform.PostReviewAsync(Request, "## Naudit Review", comments, ReviewVerdict.Approve);

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
    public async Task PostReviewAsync_everyRequest_carriesTheProjectToken()
    {
        var capture = new StubHttpMessageHandler(req =>
        {
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
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.Created, "", capture),
            Tokens("default-tok", new() { ["7"] = "proj-tok" }),
            Opts());

        await platform.PostReviewAsync(Request, "## Naudit Review",
            [new InlineComment("src/Foo.cs", 5, null, "finding")], ReviewVerdict.Approve);

        // Summary-Note, MR-Detail-GET und Discussion-POST müssen ALLE den Projekt-Token tragen.
        Assert.All(capture.Requests, r => Assert.Equal("proj-tok", r.Headers.GetValues("PRIVATE-TOKEN").Single()));
    }

    [Fact]
    public async Task GetCheckoutAsync_buildsCloneUrlWithToken_andMrRef()
    {
        const string json = """{ "http_url_to_repo": "https://gitlab.example.com/group/proj.git" }""";
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.OK, json), Tokens(), Opts());

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://oauth2:tok@gitlab.example.com/group/proj.git", info.CloneUrl);
        Assert.Equal("refs/merge-requests/42/head", info.HeadRef);
    }

    [Fact]
    public async Task PostReviewAsync_postVerdictApprove_callsApproveEndpoint()
    {
        var capture = new StubHttpMessageHandler(_ => Ok());
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(),
            Opts(postVerdict: true));

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

        Assert.Contains(capture.Calls, c =>
            c.Method == HttpMethod.Post && c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/approve"));
    }

    [Fact]
    public async Task PostReviewAsync_postVerdictApprove_skipsApprove_whenAlreadyApproved()
    {
        // GitLab antwortet 401 auf ein erneutes Approve desselben Users (kein echter Auth-Fehler).
        // Naudit prüft deshalb vorab user_has_approved und überspringt den Call — Re-Reviews
        // desselben MR bleiben idempotent.
        var capture = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/approvals")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"user_has_approved":true}""", Encoding.UTF8, "application/json"),
                }
                : Ok());
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(),
            Opts(postVerdict: true));

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

        Assert.Contains(capture.Calls, c =>
            c.Method == HttpMethod.Get && c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/approvals"));
        Assert.DoesNotContain(capture.Calls, c => c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/approve"));
    }

    [Fact]
    public async Task PostReviewAsync_postVerdictRequestChanges_callsUnapprove_andTolerates404()
    {
        // Unapprove antwortet 404, wenn es keine bestehende Approval gibt — das darf nicht werfen.
        var capture = new StubHttpMessageHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/unapprove")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ok());
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(),
            Opts(postVerdict: true));

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.RequestChanges);

        Assert.Contains(capture.Calls, c =>
            c.Method == HttpMethod.Post && c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/unapprove"));
    }

    [Fact]
    public async Task PostReviewAsync_defaultOptions_postsNoApprovalCall()
    {
        var capture = new StubHttpMessageHandler(_ => Ok());
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(),
            Opts());  // PostVerdict = false

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

        Assert.DoesNotContain(capture.Calls, c => c.Uri!.AbsolutePath.EndsWith("approve"));
    }
}
