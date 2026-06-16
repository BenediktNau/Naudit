using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Naudit.Tests;

public class WebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebhookEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Webhook_withWrongToken_returnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhook/gitlab", new { object_kind = "merge_request" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_withValidToken_andNonMergeRequestEvent_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret"))
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/gitlab")
        {
            Content = JsonContent.Create(new { object_kind = "push" }),
        };
        message.Headers.Add("X-Gitlab-Token", "test-secret");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string SignGitHub(string secret, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public async Task GitHubWebhook_withMissingSignature_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        var response = await client.PostAsJsonAsync("/webhook/github", new { action = "opened" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GitHubWebhook_withValidSignature_andNonPullRequestEvent_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        const string body = """{ "zen": "Keep it simple." }""";
        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-GitHub-Event", "ping");
        message.Headers.Add("X-Hub-Signature-256", SignGitHub("gh-secret", body));

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
