using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ReviewEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Review_withWrongToken_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitLab");
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
            })
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/review")
        {
            Content = JsonContent.Create(new { projectId = "1", mergeRequestIid = 42, title = "T" }),
        };
        message.Headers.Add("X-Naudit-Token", "wrong");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Review_withValidToken_runsReview_andReturnsVerdict()
    {
        var fakeChat = new FakeChatClient("""{"summary":"## Review\n- bug","verdict":"request_changes"}""");
        var fakeGit = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);

        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitLab");
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
                b.ConfigureServices(services =>
                {
                    services.RemoveAll<IChatClient>();
                    services.AddSingleton<IChatClient>(fakeChat);
                    services.RemoveAll<IGitPlatform>();
                    services.AddSingleton<IGitPlatform>(fakeGit);
                });
            })
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/review")
        {
            Content = JsonContent.Create(new { projectId = "1", mergeRequestIid = 42, title = "T" }),
        };
        message.Headers.Add("X-Naudit-Token", "test-secret");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<VerdictBody>();
        Assert.Equal("request_changes", body!.Verdict);
        Assert.Contains("## Review\n- bug", fakeGit.PostedMarkdown!);
    }

    private sealed record VerdictBody(string Verdict);
}
