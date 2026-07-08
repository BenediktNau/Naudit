using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Naudit.Tests;

public class SpaHostingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SpaHostingTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(bool uiEnabled)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-spa-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            if (uiEnabled)
            {
                b.UseSetting("Naudit:Ui:Enabled", "true");
                b.UseSetting("Naudit:Db:Enabled", "true");
                b.UseSetting("Naudit:Db:ConnectionString", db);
            }
        }).CreateClient();
    }

    [Fact]
    public async Task UnknownApiPath_returns404Json_neverHtmlFallback()
    {
        var response = await CreateClient(uiEnabled: true).GetAsync("/api/gibt-es-nicht");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Root_uiDisabled_stays404()
    {
        var response = await CreateClient(uiEnabled: false).GetAsync("/");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
