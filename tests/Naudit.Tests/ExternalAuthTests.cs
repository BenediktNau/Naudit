using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Naudit.Tests;

public class ExternalAuthTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public ExternalAuthTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private HttpClient CreateClient(bool gitHubEnabled)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-ext-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            if (gitHubEnabled)
            {
                b.UseSetting("Naudit:Ui:Auth:GitHub:Enabled", "true");
                b.UseSetting("Naudit:Ui:Auth:GitHub:ClientId", "test-client");
                b.UseSetting("Naudit:Ui:Auth:GitHub:ClientSecret", "test-secret");
            }
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Fact]
    public async Task GitHubChallenge_redirectsToGitHub_whenEnabled()
    {
        var response = await CreateClient(gitHubEnabled: true).GetAsync("/auth/login/github");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task GitHubChallenge_notMapped_whenDisabled()
    {
        var response = await CreateClient(gitHubEnabled: false).GetAsync("/auth/login/github");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task OidcChallenge_notMapped_whenDisabled()
    {
        var response = await CreateClient(gitHubEnabled: false).GetAsync("/auth/login/oidc");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
