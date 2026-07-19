using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GuidelinesEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public GuidelinesEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-guideapi-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "s");
            b.UseSetting("Naudit:Ai:Provider", "Ollama");
            b.UseSetting("Naudit:Ai:Model", "llama3.1");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        });
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return (client, factory);
    }

    /// <summary>Projekt seeden; liefert die Id für die Endpoint-Aufrufe.</summary>
    private static async Task<int> Seed(WebApplicationFactory<Program> factory, string project = "owner/repo")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var p = await db.Projects.SingleOrDefaultAsync(x => x.PlatformProjectId == project)
            ?? db.Projects.Add(new ProjectEntity { PlatformProjectId = project, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow }).Entity;
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>Nicht-Admin-Account mit GitHub-Link, der NICHT zum Owner des geseedeten Projekts
    /// ("owner/repo") passt — für die Fail-Closed-Regressionstests der Owner-Scoping-Branch.</summary>
    private static async Task<HttpClient> OutsiderClient(WebApplicationFactory<Program> factory)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<AccountService>();
            await svc.CreateLocalAsync("outsider", "passwort123", isAdmin: false, ["outsider"]);
        }
        var outsider = factory.CreateDefaultClient(new CookieContainerHandler());
        await outsider.PostAsJsonAsync("/auth/login", new { username = "outsider", password = "passwort123" });
        return outsider;
    }

    [Fact]
    public async Task Get_withoutSession_returns401()
    {
        var (_, factory) = await AdminApp();
        var anon = factory.CreateDefaultClient();                      // kein Login-Cookie
        var resp = await anon.GetAsync("/api/projects/1/guidelines");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_unknownProject_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.GetAsync("/api/projects/99999/guidelines");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Get_foreignProject_returns403()
    {
        var (_, factory) = await AdminApp();
        var projectId = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.GetAsync($"/api/projects/{projectId}/guidelines");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Get_noRow_returns200_withAllNullPayload()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);

        var resp = await client.GetAsync($"/api/projects/{projectId}/guidelines");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, body.GetProperty("markdown").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("distilledAt").ValueKind);
        Assert.False(body.GetProperty("manuallyEdited").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("sourcesChangedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("updatedBy").ValueKind);
    }

    [Fact]
    public async Task Put_thenGet_roundtrips_andMarksManuallyEdited()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);

        var put = await client.PutAsJsonAsync($"/api/projects/{projectId}/guidelines",
            new { markdown = "# Guidelines\nUse tabs." });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/guidelines");
        Assert.Equal("# Guidelines\nUse tabs.", get.GetProperty("markdown").GetString());
        Assert.True(get.GetProperty("manuallyEdited").GetBoolean());
        Assert.Equal("root", get.GetProperty("updatedBy").GetString());
    }

    [Fact]
    public async Task Put_emptyOrOversized_returns400()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);

        var empty = await client.PutAsJsonAsync($"/api/projects/{projectId}/guidelines", new { markdown = "" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var huge = new string('x', 4001);
        var oversized = await client.PutAsJsonAsync($"/api/projects/{projectId}/guidelines", new { markdown = huge });
        Assert.Equal(HttpStatusCode.BadRequest, oversized.StatusCode);
    }

    [Fact]
    public async Task Get_afterRedistill_exposesPendingState()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
            {
                ProjectId = projectId, Markdown = "- Regel", SourceHash = "h",
                DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
            });
            await db.SaveChangesAsync();
        }

        // Frisch destilliert: nicht pending.
        var before = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/guidelines");
        Assert.False(before.GetProperty("pending").GetBoolean());

        await client.PostAsync($"/api/projects/{projectId}/guidelines/redistill", null);

        // Nach Redistill: altes Markdown bleibt sichtbar, aber als "wird neu destilliert" markiert —
        // sonst zeigte die Karte das stale Profil wieder als fertig "distilled" an.
        var after = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/guidelines");
        Assert.True(after.GetProperty("pending").GetBoolean());
        Assert.Equal("- Regel", after.GetProperty("markdown").GetString());
    }

    [Fact]
    public async Task Redistill_resetsCurationFlags()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
            {
                ProjectId = projectId,
                Markdown = "alt",
                SourceHash = "h",
                DistilledAt = DateTime.UtcNow,
                ManuallyEdited = true,
                SourcesChangedAt = DateTime.UtcNow,
                UpdatedBy = "root",
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PostAsync($"/api/projects/{projectId}/guidelines/redistill", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("pending").GetBoolean());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            var g = await db.ProjectGuidelines.SingleAsync(x => x.ProjectId == projectId);
            Assert.False(g.ManuallyEdited);
            Assert.Equal("", g.SourceHash);
            Assert.Null(g.SourcesChangedAt);
        }
    }

    [Fact]
    public async Task Redistill_noRow_isIdempotent_returns200()
    {
        var (client, factory) = await AdminApp();
        var projectId = await Seed(factory);

        var resp = await client.PostAsync($"/api/projects/{projectId}/guidelines/redistill", null);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("pending").GetBoolean());
    }

    [Fact]
    public async Task Redistill_unknownProject_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.PostAsync("/api/projects/99999/guidelines/redistill", null);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Redistill_foreignProject_returns403()
    {
        var (_, factory) = await AdminApp();
        var projectId = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.PostAsync($"/api/projects/{projectId}/guidelines/redistill", null);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Put_unknownProject_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.PutAsJsonAsync("/api/projects/99999/guidelines", new { markdown = "# Guidelines\nUse tabs." });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_foreignProject_returns403()
    {
        var (_, factory) = await AdminApp();
        var projectId = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.PutAsJsonAsync($"/api/projects/{projectId}/guidelines", new { markdown = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }
}
