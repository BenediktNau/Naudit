using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Xunit;

namespace Naudit.Tests;

/// <summary>Status-Endpoint fürs SPA: nur gemappt bei SessionSandbox=Docker (Muster
/// GitHubAppEndpointTests) — None-Modus liefert 404 (Route fehlt), Docker-Modus 401 ohne
/// Login und 200 mit Mode+Status-Feldern eingeloggt.</summary>
public class SessionSandboxEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public SessionSandboxEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> App(bool sandboxDocker)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-sandbox-{Guid.NewGuid():N}.db")}";
        return _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "s");
            b.UseSetting("Naudit:GitHub:Token", "pat-x");
            b.UseSetting("Naudit:Ai:Provider", "Ollama");
            b.UseSetting("Naudit:Ai:Model", "llama3.1");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
            if (sandboxDocker)
            {
                b.UseSetting("Naudit:Ai:SessionSandbox", "Docker");
                // Nie erreichbar (deterministisch, kein echtes Docker im Test): Ping ⇒ false,
                // CountRunningAsync wirft ⇒ liveContainers: null.
                b.UseSetting("Naudit:Ai:Sandbox:DockerSocketPath", "/nonexistent/naudit-test.sock");
            }
        });
    }

    private static async Task<HttpClient> LoggedIn(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return client;
    }

    [Fact]
    public async Task WithoutDockerMode_routeIsNotMapped()
    {
        using var app = App(sandboxDocker: false);
        using var client = await LoggedIn(app);
        var resp = await client.GetAsync("/api/me/session-sandbox");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task DockerMode_unauthenticated_is401()
    {
        using var app = App(sandboxDocker: true);
        using var client = app.CreateClient(); // ohne Login
        var resp = await client.GetAsync("/api/me/session-sandbox");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task DockerMode_returnsModeAndStatusFields()
    {
        using var app = App(sandboxDocker: true);
        using var client = await LoggedIn(app);

        var resp = await client.GetAsync("/api/me/session-sandbox");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("Docker", json.RootElement.GetProperty("mode").GetString());
        Assert.True(json.RootElement.TryGetProperty("socketReachable", out _));
        // liveContainers ist null, wenn kein Docker-Socket erreichbar ist (CI) — Feld muss existieren.
        Assert.True(json.RootElement.TryGetProperty("liveContainers", out _));
    }
}
