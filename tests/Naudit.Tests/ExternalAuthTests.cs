using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ExternalAuthTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ExternalAuthTests(TestAppFactory factory) => _factory = factory;

    private HttpClient CreateClient(bool gitHubEnabled)
        => AuthFactory(b =>
        {
            if (gitHubEnabled)
            {
                b.UseSetting("Naudit:Ui:Auth:GitHub:Enabled", "true");
                b.UseSetting("Naudit:Ui:Auth:GitHub:ClientId", "test-client");
                b.UseSetting("Naudit:Ui:Auth:GitHub:ClientSecret", "test-secret");
            }
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task GitHubChallenge_redirectsToGitHub_whenEnabled()
    {
        var response = await CreateClient(gitHubEnabled: true).GetAsync("/auth/login/github");
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.StartsWith("https://github.com/login/oauth/authorize", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task GitHubChallenge_usesHttpsRedirectUri_behindTlsTerminatingProxy()
    {
        // Reverse-Proxy (Coolify/Traefik) terminiert TLS, die App sieht plain HTTP (TestServer =
        // http://localhost). Ohne Forwarded-Headers-Behandlung baut der OAuth-Handler die
        // redirect_uri mit http:// → Mismatch mit der bei GitHub registrierten https-Callback-URL.
        using var req = new HttpRequestMessage(HttpMethod.Get, "/auth/login/github");
        req.Headers.Add("X-Forwarded-Proto", "https");
        var response = await CreateClient(gitHubEnabled: true).SendAsync(req);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        // redirect_uri ist im authorize-Query URL-kodiert: https:// ⇒ https%3A%2F%2F.
        Assert.Contains("redirect_uri=https%3A%2F%2F", response.Headers.Location!.ToString());
        Assert.Contains("%2Fauth%2Fcallback%2Fgithub", response.Headers.Location!.ToString());
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

    // Provider aktiviert, aber Credentials leer: die OAuth-/OIDC-Handler validieren ihre Options erst
    // LAZY beim ersten Request (IAuthenticationRequestHandler, von der AuthenticationMiddleware bei
    // JEDEM Request initialisiert). Eine leere ClientId schlägt daher nicht beim Login, sondern als
    // 500 auf beliebigen Requests durch — die ganze App ist unerreichbar. Deshalb: Fail-fast beim
    // Start mit klarer, handlungsleitender Meldung.
    private WebApplicationFactory<Program> AuthFactory(Action<IWebHostBuilder> configure)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-ext-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            configure(b);
        });
    }

    [Fact]
    public void Startup_failsFast_whenGitHubAuthEnabled_withoutClientId()
    {
        var factory = AuthFactory(b => b.UseSetting("Naudit:Ui:Auth:GitHub:Enabled", "true"));
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Naudit:Ui:Auth:GitHub", ex.Message);
        Assert.Contains("ClientId", ex.Message);
    }

    [Fact]
    public void Startup_failsFast_whenGitHubAuthEnabled_withClientIdButNoSecret()
    {
        var factory = AuthFactory(b =>
        {
            b.UseSetting("Naudit:Ui:Auth:GitHub:Enabled", "true");
            b.UseSetting("Naudit:Ui:Auth:GitHub:ClientId", "abc");
        });
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("ClientSecret", ex.Message);
    }

    [Fact]
    public void Startup_failsFast_whenOidcAuthEnabled_withoutAuthority()
    {
        var factory = AuthFactory(b =>
        {
            b.UseSetting("Naudit:Ui:Auth:Oidc:Enabled", "true");
            b.UseSetting("Naudit:Ui:Auth:Oidc:ClientId", "naudit");
            b.UseSetting("Naudit:Ui:Auth:Oidc:ClientSecret", "s");
        });
        var ex = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Naudit:Ui:Auth:Oidc", ex.Message);
        Assert.Contains("Authority", ex.Message);
    }
}
