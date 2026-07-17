using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
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
}
