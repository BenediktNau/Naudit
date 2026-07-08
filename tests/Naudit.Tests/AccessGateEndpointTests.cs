using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Data;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

/// <summary>Nimmt Enqueues auf, ohne dass der Background-Worker sie konsumiert (Dequeue blockiert ewig).</summary>
public sealed class RecordingReviewQueue : IReviewQueue
{
    public List<ReviewRequest> Enqueued { get; } = [];

    public ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct = default)
    {
        Enqueued.Add(request);
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ReviewRequest> DequeueAllAsync([EnumeratorCancellation] CancellationToken ct)
    {
        // Worker bleibt idle, bis der Host beim Test-Teardown cancelt.
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        yield break;
    }
}

public class AccessGateEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public AccessGateEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static string TempDb() => $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-gate-{Guid.NewGuid():N}.db")}";

    private (HttpClient Client, RecordingReviewQueue Queue, WebApplicationFactory<Program> Factory) CreateApp(string db)
    {
        var queue = new RecordingReviewQueue();
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "hook-secret");
            b.UseSetting("Naudit:Ui:Enabled", "true");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IReviewQueue>();
                s.AddSingleton<IReviewQueue>(queue);
            });
        });
        return (factory.CreateClient(), queue, factory);
    }

    private static async Task SeedActiveOwner(WebApplicationFactory<Program> factory, string login)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var acct = new AccountEntity { Username = login, Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        acct.GitHubLinks.Add(new GitHubLinkEntity { Login = login.ToLowerInvariant() });
        db.Accounts.Add(acct);
        await db.SaveChangesAsync();
    }

    private static HttpRequestMessage SignedWebhook(string owner)
    {
        var payload = JsonSerializer.Serialize(new
        {
            action = "opened",
            pull_request = new { number = 5, title = "T" },
            repository = new { full_name = $"{owner}/repo" },
        });
        var body = Encoding.UTF8.GetBytes(payload);
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("hook-secret"));
        var sig = "sha256=" + Convert.ToHexString(h.ComputeHash(body)).ToLowerInvariant();
        var msg = new HttpRequestMessage(HttpMethod.Post, "/webhook/github")
        {
            Content = new ByteArrayContent(body),
        };
        msg.Content.Headers.ContentType = new("application/json");
        msg.Headers.Add("X-Hub-Signature-256", sig);
        msg.Headers.Add("X-GitHub-Event", "pull_request");
        return msg;
    }

    [Fact]
    public async Task Webhook_unauthorizedOwner_returns200_butDropsSilently()
    {
        var (client, queue, _) = CreateApp(TempDb());
        var response = await client.SendAsync(SignedWebhook("fremder"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Webhook_authorizedOwner_enqueues()
    {
        var db = TempDb();
        var (client, queue, factory) = CreateApp(db);
        await SeedActiveOwner(factory, "freund");

        var response = await client.SendAsync(SignedWebhook("freund"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var req = Assert.Single(queue.Enqueued);
        Assert.Equal("freund/repo", req.ProjectId);
    }

    [Fact]
    public async Task ReviewEndpoint_unauthorizedProject_returns403()
    {
        var (client, _, _) = CreateApp(TempDb());
        var msg = new HttpRequestMessage(HttpMethod.Post, "/review")
        {
            Content = JsonContent.Create(new { projectId = "fremder/repo", mergeRequestIid = 1, title = "T" }),
        };
        msg.Headers.Add("X-Naudit-Token", "hook-secret");
        var response = await client.SendAsync(msg);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
