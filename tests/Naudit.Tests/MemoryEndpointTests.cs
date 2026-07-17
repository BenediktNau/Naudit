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

public class MemoryEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public MemoryEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-memapi-{Guid.NewGuid():N}.db")}";
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

    /// <summary>Projekt + Review + 1 Finding seeden; liefert Ids für die Endpoint-Aufrufe.</summary>
    private static async Task<(int ProjectId, int ReviewId, int FindingId)> Seed(
        WebApplicationFactory<Program> factory, string project = "owner/repo")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var p = await db.Projects.SingleOrDefaultAsync(x => x.PlatformProjectId == project)
            ?? db.Projects.Add(new ProjectEntity { PlatformProjectId = project, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow }).Entity;
        var f = new ReviewFindingEntity { Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "Fund" };
        var r = new ReviewEntity
        {
            Project = p, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
            CreatedAt = DateTime.UtcNow, Findings = { f },
        };
        db.Reviews.Add(r);
        await db.SaveChangesAsync();
        return (p.Id, r.Id, f.Id);
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
    public async Task MarkFalsePositive_createsEntry_isIdempotent_andFlagsReviewDetail()
    {
        var (client, factory) = await AdminApp();
        var (projectId, reviewId, findingId) = await Seed(factory);

        var first = await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = "kein Bug" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = "immer noch keiner" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            var entry = await db.MemoryEntries.SingleAsync();          // idempotent: genau EIN Eintrag
            Assert.Equal("FalsePositive", entry.Kind);
            Assert.Equal(projectId, entry.ProjectId);
            Assert.Equal(findingId, entry.SourceFindingId);
            Assert.Equal("immer noch keiner", entry.Reason);           // Reason aktualisiert
            Assert.Equal("root", entry.CreatedBy);
            Assert.True(entry.Active);
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{reviewId}");
        var finding = detail.GetProperty("findings")[0];
        Assert.Equal(findingId, finding.GetProperty("id").GetInt32());
        Assert.True(finding.GetProperty("falsePositive").GetBoolean());
    }

    [Fact]
    public async Task UnmarkFalsePositive_deactivates_andIsIdempotent()
    {
        var (client, factory) = await AdminApp();
        var (_, reviewId, findingId) = await Seed(factory);
        await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { });

        var undo = await client.DeleteAsync($"/api/findings/{findingId}/false-positive");
        Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        var again = await client.DeleteAsync($"/api/findings/{findingId}/false-positive");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);      // kein Eintrag ⇒ trotzdem 204

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.False((await db.MemoryEntries.SingleAsync()).Active);   // deaktiviert, nicht gelöscht

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{reviewId}");
        Assert.False(detail.GetProperty("findings")[0].GetProperty("falsePositive").GetBoolean());
    }

    [Fact]
    public async Task MarkFalsePositive_unknownFinding_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.PostAsJsonAsync("/api/findings/99999/false-positive", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryApi_unauthenticated_returns401()
    {
        var (_, factory) = await AdminApp();
        var anon = factory.CreateDefaultClient();                      // kein Login-Cookie
        var resp = await anon.PostAsJsonAsync("/api/findings/1/false-positive", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Conventions_createListToggle_roundtrip()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory, "owner/conv-repo");

        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory",
            new { text = "Wir nutzen bewusst Tailwind 4", file = (string?)null });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/memory");
        var entries = list.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Convention", entries[0].GetProperty("kind").GetString());
        Assert.Equal("Wir nutzen bewusst Tailwind 4", entries[0].GetProperty("text").GetString());
        Assert.True(entries[0].GetProperty("active").GetBoolean());

        var toggle = await client.PutAsJsonAsync($"/api/memory/{entryId}", new { active = false });
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/memory");
        Assert.False(list.GetProperty("entries")[0].GetProperty("active").GetBoolean()); // inaktiv bleibt gelistet
    }

    [Fact]
    public async Task CreateConvention_emptyText_returns400()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory, "owner/conv-repo2");
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryList_unknownProject_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.GetAsync("/api/projects/99999/memory");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // --- Owner-Scoping-Regression: alle vier Memory-Routen müssen für einen aktiven,
    // nicht-admin Account ohne passenden GitHub-Link fail-closed 403 liefern
    // (CurrentAccount.CanSeeProjectAsync) — bislang nur mit dem Admin-Client getestet,
    // dessen IsAdmin-Shortcut diesen Branch nie erreicht. ---

    [Fact]
    public async Task MarkFalsePositive_nonOwner_returns403()
    {
        var (_, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task CreateConvention_nonOwner_returns403()
    {
        var (_, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.PostAsJsonAsync($"/api/projects/{projectId}/memory",
            new { text = "Sollte nicht ankommen", file = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryList_nonOwner_returns403()
    {
        var (_, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.GetAsync($"/api/projects/{projectId}/memory");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task ToggleMemory_nonOwner_returns403()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory);
        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory",
            new { text = "Konvention", file = (string?)null });
        var entryId = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var outsider = await OutsiderClient(factory);
        var resp = await outsider.PutAsJsonAsync($"/api/memory/{entryId}", new { active = false });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    // CR3: DELETE prüft jetzt Finding-Autorisierung ZUERST (wie POST) — ein Nicht-Owner darf
    // nicht mehr über 403-vs-204 die Existenz eines FP-Eintrags in fremdem Projekt erschließen.
    [Fact]
    public async Task UnmarkFalsePositive_nonOwner_returns403_evenWithoutEntry()
    {
        var (_, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);          // Finding existiert, aber KEIN FP-Eintrag

        var outsider = await OutsiderClient(factory);
        var resp = await outsider.DeleteAsync($"/api/findings/{findingId}/false-positive");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);   // vorher: 204 (Leak)
    }

    [Fact]
    public async Task UnmarkFalsePositive_unknownFinding_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.DeleteAsync("/api/findings/99999/false-positive");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // Naudit #3: Freitext-Deckel — riesige Einträge würden JEDES Folge-Review-Prompt aufblähen.
    [Fact]
    public async Task CreateConvention_tooLongText_returns400()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory);
        var huge = new string('x', 4001);
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory", new { text = huge });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MarkFalsePositive_tooLongReason_returns400()
    {
        var (client, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);
        var huge = new string('x', 4001);
        var resp = await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = huge });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
