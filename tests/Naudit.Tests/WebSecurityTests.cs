using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Web-Härtung: Same-Origin-Schutz (CSRF) für zustandsändernde /api- und /auth-Requests,
/// Login-Rate-Limit und Security-Header.</summary>
public class WebSecurityTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public WebSecurityTests(TestAppFactory factory) => _factory = factory;

    private WebApplicationFactory<Program> App(Action<IWebHostBuilder>? extra = null) =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Db:ConnectionString",
                $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-sec-{Guid.NewGuid():N}.db")}");
            extra?.Invoke(b);
        });

    private static HttpRequestMessage Post(string path, string? secFetchSite = null, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path);
        if (secFetchSite is not null) req.Headers.Add("Sec-Fetch-Site", secFetchSite);
        if (origin is not null) req.Headers.Add("Origin", origin);
        return req;
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]   // Geschwister-Subdomain: SameSite=Lax schützt hier NICHT
    public async Task UnsafeRequest_fromOtherSite_isRejected403(string site)
    {
        var client = App().CreateClient();
        var res = await client.SendAsync(Post("/auth/logout", secFetchSite: site));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]        // vom Nutzer selbst ausgelöst (Adressleiste/Lesezeichen)
    public async Task UnsafeRequest_sameOrigin_isAllowed(string site)
    {
        var client = App().CreateClient();
        var res = await client.SendAsync(Post("/auth/logout", secFetchSite: site));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_withoutFetchMetadata_foreignOrigin_isRejected403()
    {
        var client = App().CreateClient();
        var res = await client.SendAsync(Post("/auth/logout", origin: "https://evil.example.com"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_withoutFetchMetadata_ownOrigin_isAllowed()
    {
        // Production: ohne die Dev-Loopback-Ausnahme — echter Scheme+Host+Port-Vergleich.
        var client = App(b => b.UseEnvironment("Production")).CreateClient(); // TestServer: http://localhost
        var res = await client.SendAsync(Post("/auth/logout", origin: "http://localhost"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        var otherPort = await client.SendAsync(Post("/auth/logout", origin: "http://localhost:5173"));
        Assert.Equal(HttpStatusCode.Forbidden, otherPort.StatusCode);
    }

    [Fact]
    public async Task UnsafeRequest_withoutAnyBrowserHeaders_isAllowed()
    {
        // curl/CI/Skripte senden weder Sec-Fetch-Site noch Origin — kein Browser, kein CSRF.
        var client = App().CreateClient();
        var res = await client.SendAsync(Post("/auth/logout"));
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    [Fact]
    public async Task SafeRequest_crossSite_isNotBlocked()
    {
        var client = App().CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        req.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task MachineEndpoints_andAuthCallbacks_areExempt()
    {
        var client = App().CreateClient();
        // Webhook: cross-site ⇒ trotzdem die eigene Auth-Prüfung (401), nicht der CSRF-Block (403).
        var hook = await client.SendAsync(Post("/webhook/gitlab", secFetchSite: "cross-site"));
        Assert.Equal(HttpStatusCode.Unauthorized, hook.StatusCode);
        var review = Post("/review", secFetchSite: "cross-site");
        review.Content = JsonContent.Create(new { projectId = "p", mergeRequestIid = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(review)).StatusCode);
        // OIDC form_post-Callback kommt legitim cross-site vom IdP.
        var callback = await client.SendAsync(Post("/auth/callback/oidc", secFetchSite: "cross-site"));
        Assert.NotEqual(HttpStatusCode.Forbidden, callback.StatusCode);
    }

    [Fact]
    public async Task Login_isRateLimited_perClient_429()
    {
        var client = App(b =>
        {
            b.UseSetting("Naudit:Ui:LoginRateLimit:PermitLimit", "3");
            b.UseSetting("Naudit:Ui:LoginRateLimit:WindowSeconds", "60");
        }).CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var res = await client.PostAsJsonAsync("/auth/login", new { username = "x", password = "falsch-123" });
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }
        var limited = await client.PostAsJsonAsync("/auth/login", new { username = "x", password = "falsch-123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Responses_carrySecurityHeaders()
    {
        var client = App().CreateClient();
        var res = await client.GetAsync("/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }
}
