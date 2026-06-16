using System.Text.Json;
using Microsoft.Extensions.Options;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNauditInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
builder.Services.AddHostedService<ReviewBackgroundService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

// Nur den Webhook-Endpoint der aktiven Plattform mappen.
var platform = app.Services.GetRequiredService<GitOptions>().Platform;

if (platform == GitPlatformKind.GitHub)
{
    app.MapPost("/webhook/github", async (HttpContext context, IReviewQueue queue, IOptions<GitHubOptions> gitHubOptions) =>
    {
        // Rohen Body lesen (Bytes) — die HMAC-Signatur geht über die exakten Bytes.
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms);
        var rawBody = ms.ToArray();

        var signature = context.Request.Headers["X-Hub-Signature-256"].ToString();
        if (!GitHubWebhook.IsValidSignature(rawBody, gitHubOptions.Value.WebhookSecret, signature))
            return Results.Unauthorized();

        var eventType = context.Request.Headers["X-GitHub-Event"].ToString();
        var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(rawBody);
        if (payload is null)
            return Results.Ok();

        var request = GitHubWebhook.ToReviewRequest(eventType, payload);
        if (request is null)
            return Results.Ok(); // kein pull_request-Event oder keine reviewbare Aktion

        await queue.EnqueueAsync(request);
        return Results.Ok();
    });
}
else // GitPlatformKind.GitLab
{
    app.MapPost("/webhook/gitlab", async (HttpContext context, IReviewQueue queue, IOptions<GitLabOptions> gitLabOptions) =>
    {
        var secret = gitLabOptions.Value.WebhookSecret;
        var token = context.Request.Headers["X-Gitlab-Token"].ToString();
        if (string.IsNullOrEmpty(secret) || token != secret)
            return Results.Unauthorized();

        var payload = await context.Request.ReadFromJsonAsync<GitLabWebhookPayload>();
        if (payload is null)
            return Results.Ok();

        var request = GitLabWebhook.ToReviewRequest(payload);
        if (request is null)
            return Results.Ok(); // kein MR-Event oder keine reviewbare Aktion

        await queue.EnqueueAsync(request);
        return Results.Ok();
    });
}

app.Run();

// Hinweis: In .NET 10 ist die generierte Program-Klasse automatisch public,
// daher von WebApplicationFactory<Program> im Testprojekt direkt nutzbar.
