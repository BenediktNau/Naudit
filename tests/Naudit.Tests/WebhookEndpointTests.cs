using System.Net;
using System.Net.Http.Json;
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
}
