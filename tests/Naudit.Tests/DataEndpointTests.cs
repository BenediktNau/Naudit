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

public class DataEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public DataEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-data-{Guid.NewGuid():N}.db")}";
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

    /// <summary>Projekt + 1 Review direkt in die DB legen (schneller als über den Sink).</summary>
    private static async Task<int> SeedReview(WebApplicationFactory<Program> factory, string project, long tokens)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var p = await db.Projects.SingleOrDefaultAsync(x => x.PlatformProjectId == project)
            ?? db.Projects.Add(new ProjectEntity { PlatformProjectId = project, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow }).Entity;
        var r = new ReviewEntity
        {
            Project = p, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
            InputTokens = tokens, OutputTokens = 0, Model = "m", CreatedAt = DateTime.UtcNow,
            Findings = { new ReviewFindingEntity { Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "f" } },
        };
        db.Reviews.Add(r);
        await db.SaveChangesAsync();
        return r.Id;
    }

    [Fact]
    public async Task Dashboard_aggregatesTokensAndProjects()
    {
        var (client, factory) = await AdminApp();
        await SeedReview(factory, "owner/repo-a", 1000);
        await SeedReview(factory, "owner/repo-b", 500);

        var dash = await client.GetFromJsonAsync<JsonElement>("/api/dashboard");

        Assert.Equal(1500, dash.GetProperty("tokensMonth").GetInt64());
        Assert.Equal(2, dash.GetProperty("reviewsTotal").GetInt32());
        Assert.Equal(2, dash.GetProperty("projectsTotal").GetInt32());
        Assert.Equal(30, dash.GetProperty("tokensPerDay").GetArrayLength());
        Assert.Equal(2, dash.GetProperty("projects").GetArrayLength());
        Assert.Equal(2, dash.GetProperty("recentReviews").GetArrayLength());
    }

    [Fact]
    public async Task ReviewDetail_returnsFindings_and404ForUnknown()
    {
        var (client, factory) = await AdminApp();
        var id = await SeedReview(factory, "owner/repo", 100);

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{id}");
        Assert.Equal("owner/repo", detail.GetProperty("project").GetString());
        Assert.Equal(1, detail.GetProperty("findings").GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/reviews/9999")).StatusCode);
    }

    [Fact]
    public async Task MeUsage_returnsSixMonths()
    {
        var (client, factory) = await AdminApp();
        await SeedReview(factory, "owner/repo", 2000);
        var usage = await client.GetFromJsonAsync<JsonElement>("/api/me/usage");
        Assert.Equal(6, usage.GetProperty("monthly").GetArrayLength());
        Assert.Equal(1, usage.GetProperty("reviewsTotal").GetInt32());
    }

    [Fact]
    public async Task Revoke_activeAdmin_isRejected_toPreventLockout()
    {
        var (client, factory) = await AdminApp();
        int rootId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            rootId = (await db.Accounts.SingleAsync(a => a.Username == "root")).Id;
        }

        var res = await client.PostAsync($"/api/accounts/{rootId}/revoke", null);
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            Assert.Equal(AccountStatus.Active, (await db.Accounts.SingleAsync(a => a.Username == "root")).Status);
        }
    }

    [Fact]
    public async Task Revoke_normalActiveUser_succeeds()
    {
        var (client, _) = await AdminApp();
        var created = await client.PostAsJsonAsync("/api/accounts",
            new { username = "worker", password = "passwort123", isAdmin = false });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var res = await client.PostAsync($"/api/accounts/{id}/revoke", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task DataEndpoints_require401ForAnonymous()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-data-{Guid.NewGuid():N}.db")}";
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:ConnectionString", db);
        }).CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/dashboard")).StatusCode);
    }
}
