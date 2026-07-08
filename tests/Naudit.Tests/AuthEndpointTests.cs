using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Naudit.Tests;

public class AuthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AuthEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private (HttpClient Client, WebApplicationFactory<Program> Factory) CreateApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-auth-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        });
        // CookieContainerHandler: Session-Cookie überlebt zwischen Requests (BFF-Flow).
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        return (client, factory);
    }

    [Fact]
    public async Task Me_anonymous_reportsProvidersAndUnauthenticated()
    {
        var (client, _) = CreateApp();
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.True(me.GetProperty("authProviders").GetProperty("local").GetBoolean());
        Assert.False(me.GetProperty("authProviders").GetProperty("gitHub").GetBoolean());
    }

    [Fact]
    public async Task Login_wrongPassword_401_correct_setsSessionAndMeShowsAdmin()
    {
        var (client, _) = CreateApp();

        var wrong = await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "falsch" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var ok = await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.True(me.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("root", me.GetProperty("username").GetString());
        Assert.True(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal("Active", me.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Logout_endsSession()
    {
        var (client, _) = CreateApp();
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        await client.PostAsync("/auth/logout", null);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/me");
        Assert.False(me.GetProperty("isAuthenticated").GetBoolean());
    }

    [Fact]
    public async Task Login_persistsDataProtectionKeys_inDatabase()
    {
        // Erster Login erzeugt den Key-Ring lazy — der Signatur-Key muss in der DB landen,
        // damit Sessions Container-Neustarts überleben (beide Backends, kein Key-Verzeichnis).
        var (client, factory) = CreateApp();
        var ok = await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Data.NauditDbContext>();
        Assert.True(await db.DataProtectionKeys.AnyAsync());
    }

    [Fact]
    public async Task UiDisabled_authEndpointsNotMapped()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        }).CreateClient();
        var response = await client.PostAsJsonAsync("/auth/login", new { username = "x", password = "y" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
