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

public class ResolutionEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ResolutionEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-resapi-{Guid.NewGuid():N}.db")}";
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
    /// ("owner/repo") passt — für den Fail-Closed-Test der Owner-Scoping-Branch.</summary>
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
    public async Task Put_withoutSession_returns401()
    {
        var (_, factory) = await AdminApp();
        var anon = factory.CreateDefaultClient();                      // kein Login-Cookie
        var resp = await anon.PutAsJsonAsync("/api/findings/1/resolution", new { status = "Accepted" });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Put_unknownFinding_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.PutAsJsonAsync("/api/findings/99999/resolution", new { status = "Accepted" });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Put_foreignProject_returns403()
    {
        var (_, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);
        var outsider = await OutsiderClient(factory);

        var resp = await outsider.PutAsJsonAsync($"/api/findings/{findingId}/resolution", new { status = "Accepted" });
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Put_accepted_thenReviewDetail_showsResolutionStatus()
    {
        var (client, factory) = await AdminApp();
        var (_, reviewId, findingId) = await Seed(factory);

        var put = await client.PutAsJsonAsync($"/api/findings/{findingId}/resolution", new { status = "Accepted" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{reviewId}");
        var finding = detail.GetProperty("findings")[0];
        Assert.Equal(findingId, finding.GetProperty("id").GetInt32());
        Assert.Equal("Accepted", finding.GetProperty("resolutionStatus").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var entity = await db.ReviewFindings.SingleAsync(f => f.Id == findingId);
        Assert.Equal("Accepted", entity.ResolutionStatus);
        Assert.Equal("WebUi", entity.ResolutionSource);
        Assert.Equal("root", entity.ResolvedBy);
    }

    [Fact]
    public async Task Put_invalidStatus_returns400()
    {
        var (client, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);

        var resp = await client.PutAsJsonAsync($"/api/findings/{findingId}/resolution", new { status = "Maybe" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Put_null_undoesOwnWebUi()
    {
        var (client, factory) = await AdminApp();
        var (_, _, findingId) = await Seed(factory);

        var set = await client.PutAsJsonAsync($"/api/findings/{findingId}/resolution", new { status = "Accepted" });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);

        var undo = await client.PutAsJsonAsync($"/api/findings/{findingId}/resolution", new { status = (string?)null });
        Assert.Equal(HttpStatusCode.OK, undo.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var entity = await db.ReviewFindings.SingleAsync(f => f.Id == findingId);
        Assert.Null(entity.ResolutionStatus);
        Assert.Null(entity.ResolutionSource);
    }
}
