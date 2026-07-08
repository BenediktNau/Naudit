using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SpaHostingTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SpaHostingTests(TestAppFactory factory) => _factory = factory;

    private HttpClient CreateClient()
    {
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        }).CreateClient();
    }

    [Fact]
    public async Task UnknownApiPath_returns404Json_neverHtmlFallback()
    {
        var response = await CreateClient().GetAsync("/api/gibt-es-nicht");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }
}
