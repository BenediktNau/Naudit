using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Naudit.Core.Models;
using Naudit.Core.Review;
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

// CI/CD-Trigger: synchroner Review mit strukturiertem Verdict (Merge-Gate).
// Immer gemappt, unabhängig von der aktiven Plattform. Auth = Webhook-Secret als Header-Token.
app.MapPost("/review", async (
    HttpContext context,
    ReviewTriggerRequest body,
    GitOptions gitOptions,
    IOptions<GitLabOptions> gitLabOptions,
    IOptions<GitHubOptions> gitHubOptions,
    CancellationToken ct) =>
{
    var secret = gitOptions.Platform == GitPlatformKind.GitHub
        ? gitHubOptions.Value.WebhookSecret
        : gitLabOptions.Value.WebhookSecret;

    var token = context.Request.Headers["X-Naudit-Token"].ToString();
    if (!IsValidNauditToken(secret, token))
        return Results.Unauthorized();

    // ReviewService erst nach bestandener Auth auflösen (Scope-Service, inline statt Queue).
    var reviewService = context.RequestServices.GetRequiredService<ReviewService>();
    var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty);
    var result = await reviewService.ReviewAsync(request, ct);

    var verdict = result.Verdict == ReviewVerdict.RequestChanges ? "request_changes" : "approve";
    return Results.Ok(new { verdict });
});

// Konstant-zeitlicher Vergleich; leeres Secret oder leerer Token ⇒ false (fail-closed).
static bool IsValidNauditToken(string? secret, string? provided)
{
    if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(provided))
        return false;
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(provided));
}

app.Run();

// Hinweis: In .NET 10 ist die generierte Program-Klasse automatisch public,
// daher von WebApplicationFactory<Program> im Testprojekt direkt nutzbar.

/// <summary>Request-Body des CI-Triggers; wird direkt auf ReviewRequest gemappt
/// (bei GitHub ist ProjectId = "owner/repo" und MergeRequestIid = PR-Nummer).</summary>
public sealed record ReviewTriggerRequest(string ProjectId, int MergeRequestIid, string? Title);
