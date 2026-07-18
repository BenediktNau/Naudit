using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class WebhookEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;

    public WebhookEndpointTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task Webhook_withWrongToken_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Naudit:Git:Platform", "GitLab"))
            .CreateClient();

        var response = await client.PostAsJsonAsync("/webhook/gitlab", new { object_kind = "merge_request" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_withValidToken_andNonMergeRequestEvent_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitLab");
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
            })
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

    [Fact]
    public async Task GitHubWebhook_withWrongSignature_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        const string body = """{ "action": "opened" }""";
        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-GitHub-Event", "pull_request");
        message.Headers.Add("X-Hub-Signature-256", SignGitHub("wrong-secret", body));

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GitHubWebhook_reviewComment_nonCommand_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        // Gültige Signatur, aber Body ist KEIN Kommando ⇒ Mapping null ⇒ 200, ohne Plattform-Call.
        const string body = """
            { "action": "created",
              "repository": { "full_name": "acme/widgets" },
              "pull_request": { "number": 7 },
              "comment": { "id": 999, "in_reply_to_id": 555, "body": "thanks!", "author_association": "MEMBER" } }
            """;
        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-GitHub-Event", "pull_request_review_comment");
        message.Headers.Add("X-Hub-Signature-256", SignGitHub("gh-secret", body));

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GitHubWebhook_reviewComment_badSignature_returnsUnauthorized()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitHub");
                b.UseSetting("Naudit:GitHub:WebhookSecret", "gh-secret");
            })
            .CreateClient();

        const string body = """{ "action": "created", "comment": { "in_reply_to_id": 555, "body": "@naudit fp" } }""";
        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        message.Headers.Add("X-GitHub-Event", "pull_request_review_comment");
        message.Headers.Add("X-Hub-Signature-256", SignGitHub("wrong-secret", body));

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);   // Signatur vor Comment-Handling
    }

    [Fact]
    public async Task GitLabWebhook_note_nonCommand_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("Naudit:Git:Platform", "GitLab");
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
            })
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/gitlab")
        {
            Content = JsonContent.Create(new
            {
                object_kind = "note",
                user = new { id = 42, username = "bob" },
                project = new { id = 7 },
                merge_request = new { iid = 13 },
                object_attributes = new { note = "thanks", noteable_type = "MergeRequest", discussion_id = "abc123" },
            }),
        };
        message.Headers.Add("X-Gitlab-Token", "test-secret");

        var response = await client.SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
