using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Git.GitHub;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public GitHubAppEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private sealed class FakeChecker : IGitHubAppInstallationChecker
    {
        public ValueTask<GitHubInstallationStatus> GetStatusAsync(IReadOnlyList<string> logins, CancellationToken ct = default)
            => ValueTask.FromResult(new GitHubInstallationStatus(
                "https://github.com/apps/naudit/installations/new",
                [new GitHubLoginInstallation("octocat", false)]));
    }

    // App-Modus verlangt einen echten Private Key (Fail-fast in AddNauditInfrastructure).
    private static string TestPem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    private WebApplicationFactory<Program> App(bool authApp, bool withFakeChecker = true)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-ghapp-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "s");
            b.UseSetting("Naudit:Ai:Provider", "Ollama");
            b.UseSetting("Naudit:Ai:Model", "llama3.1");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
            if (authApp)
            {
                b.UseSetting("Naudit:GitHub:Auth", "App");
                b.UseSetting("Naudit:GitHub:App:AppId", "12345");
                b.UseSetting("Naudit:GitHub:App:PrivateKey", TestPem());
            }
            else
            {
                // Pat-Modus mit vollstaendiger Pflicht-Config: sonst startet der Host im Setup-Modus
                // (GitHub:Token fehlt) und /api/me/github-app waere schon deshalb ungemappt. Mit Token
                // ist reviewActive=true, und die 404 beweist die Auth!=App-Selbstabschaltung des Endpoints.
                b.UseSetting("Naudit:GitHub:Token", "pat-x");
            }
            if (withFakeChecker)
                b.ConfigureTestServices(s => s.AddSingleton<IGitHubAppInstallationChecker>(new FakeChecker()));
        });
    }

    private static async Task<HttpClient> LoggedIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return client;
    }

    [Fact]
    public async Task GitHubApp_returnsInstallStatus_whenAuthApp()
    {
        var client = await LoggedIn(App(authApp: true));
        var res = await client.GetAsync("/api/me/github-app");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://github.com/apps/naudit/installations/new", body.GetProperty("installUrl").GetString());
        var first = body.GetProperty("accounts")[0];
        Assert.Equal("octocat", first.GetProperty("login").GetString());
        Assert.False(first.GetProperty("installed").GetBoolean());
    }

    [Fact]
    public async Task GitHubApp_notMapped_whenAuthIsPat()
    {
        var client = await LoggedIn(App(authApp: false, withFakeChecker: false));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me/github-app")).StatusCode);
    }

    [Fact]
    public async Task GitHubApp_requires401ForAnonymous()
    {
        var client = App(authApp: true).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me/github-app")).StatusCode);
    }
}
