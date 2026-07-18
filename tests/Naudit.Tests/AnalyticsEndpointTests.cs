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

public class AnalyticsEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public AnalyticsEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-analytics-{Guid.NewGuid():N}.db")}";
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

    /// <summary>Projekt (per Namen wiederverwendet) + 1 Review mit den angegebenen Findings
    /// (Severity, ResolutionStatus) seeden; liefert die Projekt-Id.</summary>
    private static async Task<int> SeedReview(
        WebApplicationFactory<Program> factory, string project, DateTime createdAt,
        params (string Severity, string? ResolutionStatus)[] findings)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var p = await db.Projects.SingleOrDefaultAsync(x => x.PlatformProjectId == project)
            ?? db.Projects.Add(new ProjectEntity { PlatformProjectId = project, FirstReviewedAt = createdAt, LastReviewedAt = createdAt }).Entity;
        var r = new ReviewEntity
        {
            Project = p, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S", CreatedAt = createdAt,
        };
        foreach (var (severity, resolution) in findings)
            r.Findings.Add(new ReviewFindingEntity { Severity = severity, Confidence = "High", File = "a.cs", Line = 1, Text = "f", ResolutionStatus = resolution });
        db.Reviews.Add(r);
        await db.SaveChangesAsync();
        return p.Id;
    }

    /// <summary>Nicht-Admin-Account mit GitHub-Link "outsider" — passt zu keinem "owner/..."-Projekt
    /// (wie in MemoryEndpointTests, hier für die Sichtbarkeits-Regression der Analytics-API).</summary>
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
        var anon = factory.CreateDefaultClient();   // kein Login-Cookie
        var resp = await anon.GetAsync("/api/analytics");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Get_invalidDays_returns400()
    {
        var (client, _) = await AdminApp();
        var resp = await client.GetAsync("/api/analytics?days=5");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Get_totals_computeRatesAndUnanswered()
    {
        var (client, factory) = await AdminApp();
        var mixedId = await SeedReview(factory, "owner/mixed", DateTime.UtcNow,
            ("High", "Accepted"), ("Medium", "Rejected"), ("Low", null));

        var res = await client.GetFromJsonAsync<JsonElement>($"/api/analytics?projectId={mixedId}&days=30");
        var totals = res.GetProperty("totals");
        Assert.Equal(3, totals.GetProperty("posted").GetInt32());
        Assert.Equal(1, totals.GetProperty("accepted").GetInt32());
        Assert.Equal(1, totals.GetProperty("rejected").GetInt32());
        Assert.Equal(1, totals.GetProperty("unanswered").GetInt32());
        Assert.Equal(1.0 / 3.0, totals.GetProperty("acceptanceRate").GetDouble(), 3);
        Assert.Equal(1.0 / 3.0, totals.GetProperty("fpRate").GetDouble(), 3);

        // Zweites, leeres Projekt: Review ohne Findings ⇒ Raten 0 statt Division-durch-0.
        var emptyId = await SeedReview(factory, "owner/empty", DateTime.UtcNow);
        var res2 = await client.GetFromJsonAsync<JsonElement>($"/api/analytics?projectId={emptyId}&days=30");
        var totals2 = res2.GetProperty("totals");
        Assert.Equal(0, totals2.GetProperty("posted").GetInt32());
        Assert.Equal(0, totals2.GetProperty("acceptanceRate").GetDouble());
        Assert.Equal(0, totals2.GetProperty("fpRate").GetDouble());
    }

    [Fact]
    public async Task Get_bySeverity_breaksDown()
    {
        var (client, factory) = await AdminApp();
        var projectId = await SeedReview(factory, "owner/sev", DateTime.UtcNow,
            ("High", "Accepted"), ("High", null), ("Low", "Rejected"));

        var res = await client.GetFromJsonAsync<JsonElement>($"/api/analytics?projectId={projectId}&days=30");
        var bySeverity = res.GetProperty("bySeverity").EnumerateArray().ToList();
        Assert.Equal(2, bySeverity.Count);   // nur high + low — leere Severities werden gefiltert

        var high = bySeverity.Single(x => x.GetProperty("severity").GetString() == "high");
        Assert.Equal(2, high.GetProperty("posted").GetInt32());
        Assert.Equal(1, high.GetProperty("accepted").GetInt32());
        Assert.Equal(0, high.GetProperty("rejected").GetInt32());

        var low = bySeverity.Single(x => x.GetProperty("severity").GetString() == "low");
        Assert.Equal(1, low.GetProperty("posted").GetInt32());
        Assert.Equal(1, low.GetProperty("rejected").GetInt32());
    }

    [Fact]
    public async Task Get_weekly_bucketsByIsoWeek()
    {
        var (client, factory) = await AdminApp();
        // Datum und Datum-7-Tage liegen IMMER in verschiedenen ISO-Wochen (eine Woche = eine ISO-Woche).
        var projectId = await SeedReview(factory, "owner/weekly", DateTime.UtcNow, ("High", null));
        await SeedReview(factory, "owner/weekly", DateTime.UtcNow.AddDays(-7), ("High", null));

        var res = await client.GetFromJsonAsync<JsonElement>($"/api/analytics?projectId={projectId}&days=30");
        var weekly = res.GetProperty("weekly").EnumerateArray().ToList();
        Assert.Equal(2, weekly.Count);
        Assert.All(weekly, w => Assert.Equal(1, w.GetProperty("posted").GetInt32()));
    }

    [Fact]
    public async Task Get_foreignProject_notCounted()
    {
        var (_, factory) = await AdminApp();
        var foreignId = await SeedReview(factory, "owner/foreign", DateTime.UtcNow, ("High", "Accepted"));
        var outsider = await OutsiderClient(factory);

        // Explizite Projekt-Id eines fremden Projekts ⇒ 403.
        var resp = await outsider.GetAsync($"/api/analytics?projectId={foreignId}&days=30");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);

        // Aggregiert (keine projectId): das fremde Projekt zählt nicht mit.
        var agg = await outsider.GetFromJsonAsync<JsonElement>("/api/analytics?days=30");
        Assert.Equal(0, agg.GetProperty("totals").GetProperty("posted").GetInt32());
    }

    [Fact]
    public async Task Get_memory_countsEntriesActiveTimesApplied()
    {
        var (client, factory) = await AdminApp();
        var projectId = await SeedReview(factory, "owner/mem", DateTime.UtcNow, ("High", null));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            db.MemoryEntries.AddRange(
                new MemoryEntryEntity { ProjectId = projectId, Kind = "Convention", Text = "a", CreatedBy = "root", CreatedAt = DateTime.UtcNow, Active = true, TimesApplied = 3 },
                new MemoryEntryEntity { ProjectId = projectId, Kind = "FalsePositive", Text = "b", CreatedBy = "root", CreatedAt = DateTime.UtcNow, Active = false, TimesApplied = 2 });
            await db.SaveChangesAsync();
        }

        var res = await client.GetFromJsonAsync<JsonElement>($"/api/analytics?projectId={projectId}&days=30");
        var memory = res.GetProperty("memory");
        Assert.Equal(2, memory.GetProperty("entries").GetInt32());
        Assert.Equal(1, memory.GetProperty("active").GetInt32());
        Assert.Equal(5, memory.GetProperty("timesApplied").GetInt32());
    }
}
