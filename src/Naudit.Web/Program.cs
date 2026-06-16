using Microsoft.Extensions.Options;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNauditInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
builder.Services.AddHostedService<ReviewBackgroundService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

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

app.Run();

// Hinweis: In .NET 10 ist die generierte Program-Klasse automatisch public,
// daher von WebApplicationFactory<Program> im Testprojekt direkt nutzbar.
