using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AuthorLoginResolverTests
{
    private static readonly ReviewRequest GitLabRequest = new("42", 7, "T");

    private sealed class FixedTokenProvider : IGitTokenProvider
    {
        public ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
            => ValueTask.FromResult("glpat-test");
    }

    private static GitLabAuthorLoginResolver Resolver(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.com/") },
            new FixedTokenProvider(), NullLogger<GitLabAuthorLoginResolver>.Instance);

    [Fact]
    public async Task Passthrough_returnsRequestAuthorLogin()
    {
        var resolver = new PassthroughAuthorLoginResolver();
        Assert.Equal("alice", await resolver.ResolveAsync(new ReviewRequest("o/r", 1, "T", "alice")));
        Assert.Null(await resolver.ResolveAsync(new ReviewRequest("o/r", 1, "T")));
    }

    [Fact]
    public async Task GitLab_fetchesAuthorUsername_fromMrApi()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            Assert.EndsWith("api/v4/projects/42/merge_requests/7", req.RequestUri!.AbsolutePath);
            Assert.Equal("glpat-test", req.Headers.GetValues("PRIVATE-TOKEN").Single());
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"author":{"username":"alice"}}""", System.Text.Encoding.UTF8, "application/json"),
            };
        });

        Assert.Equal("alice", await Resolver(handler).ResolveAsync(GitLabRequest));
    }

    [Fact]
    public async Task GitLab_requestAlreadyHasLogin_skipsHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("kein HTTP erwartet"));
        Assert.Equal("bob", await Resolver(handler).ResolveAsync(new ReviewRequest("42", 7, "T", "bob")));
    }

    [Fact]
    public async Task GitLab_apiError_returnsNull_failQuiet()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        Assert.Null(await Resolver(handler).ResolveAsync(GitLabRequest));
    }
}
