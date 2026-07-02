using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubPlatformTests
{
    private static readonly ReviewRequest Request = new("octo/hello-world", 42, "Title");

    // Token-Provider für die Tests: Default-Token, optional Per-Projekt-Overrides ("owner/repo" → Token).
    private static IGitTokenProvider Tokens(string @default = "tok", Dictionary<string, string>? map = null)
        => new ConfiguredGitTokenProvider(@default,
            (map ?? new()).Select(kv => new ProjectTokenEntry { Project = kv.Key, Token = kv.Value }));

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
    }

    // Options-Helper: Default = PostVerdict false (heutiges Verhalten).
    private static IOptions<GitHubOptions> Opts(bool postVerdict = false)
        => Options.Create(new GitHubOptions { PostVerdict = postVerdict });

    [Fact]
    public async Task GetChangesAsync_mapsFilesFromApi_andSkipsFilesWithoutPatch()
    {
        const string json = """
        [
          { "filename": "src/Foo.cs", "patch": "@@ +1 @@\n+x" },
          { "filename": "assets/logo.png" }
        ]
        """;
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, json), Tokens(), Opts());

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
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "[]", capture), Tokens(), Opts());

        await platform.GetChangesAsync(Request);

        Assert.Contains("repos/octo/hello-world/pulls/42/files", capture.LastRequest!.RequestUri!.ToString());
        // per_page=100 ist die bewusste POC-Paginierungsgrenze — explizit verankern.
        Assert.Contains("per_page=100", capture.LastRequest.RequestUri!.ToString());
    }

    [Fact]
    public async Task GetChangesAsync_usesPerProjectToken_inAuthHeader()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var platform = new GitHubPlatform(
            ClientReturning(HttpStatusCode.OK, "[]", capture),
            Tokens("default-tok", new() { ["octo/hello-world"] = "proj-tok" }),
            Opts());

        await platform.GetChangesAsync(Request);  // Request.ProjectId == "octo/hello-world"

        var auth = capture.LastRequest!.Headers.Authorization!;
        Assert.Equal("Bearer", auth.Scheme);
        Assert.Equal("proj-tok", auth.Parameter);
    }

    [Fact]
    public async Task GetChangesAsync_unmappedProject_fallsBackToDefaultToken()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json"),
        });
        var platform = new GitHubPlatform(
            ClientReturning(HttpStatusCode.OK, "[]", capture),
            Tokens("default-tok", new() { ["octo/other"] = "proj-tok" }),
            Opts());

        await platform.GetChangesAsync(Request);

        Assert.Equal("default-tok", capture.LastRequest!.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task PostReviewAsync_withoutComments_postsReviewBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(), Opts());

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("repos/octo/hello-world/pulls/42/reviews", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
        Assert.Contains("\"event\":\"COMMENT\"", capture.LastRequestBody!);
    }

    [Fact]
    public async Task PostReviewAsync_withComments_includesPathLineSide()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.Created, "", capture), Tokens(), Opts());

        await platform.PostReviewAsync(Request, "## Naudit Review",
            [new InlineComment("src/Foo.cs", 5, null, "finding here")], ReviewVerdict.Approve);

        var body = capture.LastRequestBody!;
        Assert.Contains("repos/octo/hello-world/pulls/42/reviews", capture.LastRequest!.RequestUri!.ToString());
        Assert.Contains("\"path\":\"src/Foo.cs\"", body);
        Assert.Contains("\"line\":5", body);
        Assert.Contains("\"side\":\"RIGHT\"", body);
        Assert.Contains("finding here", body);
    }

    [Fact]
    public async Task GetCheckoutAsync_buildsCloneUrlWithToken_andPrRef()
    {
        const string json = """{ "clone_url": "https://github.com/owner/repo.git" }""";
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, json), Tokens(), Opts());

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://x-access-token:tok@github.com/owner/repo.git", info.CloneUrl);
        Assert.Equal("refs/pull/42/head", info.HeadRef);
    }

    [Fact]
    public async Task GetCheckoutAsync_usesPerProjectToken_inCloneUrl()
    {
        const string json = """{ "clone_url": "https://github.com/owner/repo.git" }""";
        var platform = new GitHubPlatform(
            ClientReturning(HttpStatusCode.OK, json),
            Tokens("default-tok", new() { ["octo/hello-world"] = "proj-tok" }),
            Opts());

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://x-access-token:proj-tok@github.com/owner/repo.git", info.CloneUrl);
    }

    [Fact]
    public async Task PostReviewAsync_defaultOptions_keepsEventComment()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "{}", capture), Tokens(),
            Opts());  // PostVerdict = false (Default)

        await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.RequestChanges);

        Assert.Contains("\"event\":\"COMMENT\"", capture.LastRequestBody);
    }

    [Theory]
    [InlineData(ReviewVerdict.Approve, "APPROVE")]
    [InlineData(ReviewVerdict.RequestChanges, "REQUEST_CHANGES")]
    public async Task PostReviewAsync_postVerdict_mapsVerdictToEvent(ReviewVerdict verdict, string expectedEvent)
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "{}", capture), Tokens(),
            Opts(postVerdict: true));

        await platform.PostReviewAsync(Request, "## Naudit Review", [], verdict);

        Assert.Contains($"\"event\":\"{expectedEvent}\"", capture.LastRequestBody);
    }
}
