using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Web;
using Naudit.Web.Endpoints;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNauditInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
builder.Services.AddHostedService<ReviewBackgroundService>();

// WebUI-Auth (BFF): Cookie-Session; API-Verhalten statt Browser-Redirects (401/403 als Status).
var uiConfig = builder.Configuration.GetSection("Naudit:Ui").Get<Naudit.Infrastructure.Ui.UiOptions>()
    ?? new Naudit.Infrastructure.Ui.UiOptions();
if (uiConfig.Enabled)
{
    var auth = builder.Services
        .AddAuthentication(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.Cookie.Name = "naudit.session";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            // Produktion: Always — hinter einem TLS-terminierenden Reverse-Proxy (Coolify/nginx) erreicht
            // die App plain HTTP, der Browser spricht aber HTTPS; das Session-Cookie darf nie ohne
            // Secure-Flag raus. Development (lokales http-Dev / TestServer): SameAsRequest, sonst käme das
            // Secure-Cookie über http nie zurück.
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            o.SlidingExpiration = true;
            o.ExpireTimeSpan = TimeSpan.FromDays(7);
            // SPA-Kontrakt: kein Redirect auf eine Login-SEITE — 401/403 sprechen für sich.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        });
    builder.Services.AddAuthorization();

    // Data-Protection-Keys (Session-Cookie-Signatur) in die DB: überleben Container-Neustarts
    // auf beiden Backends, kein Key-Verzeichnis/Volume nötig. UI ⇒ DB garantiert den DbContext.
    builder.Services.AddDataProtection()
        .PersistKeysToDbContext<Naudit.Infrastructure.Data.NauditDbContext>();

    if (uiConfig.Auth.GitHub.Enabled)
    {
        auth.AddOAuth("GitHub", o =>
        {
            o.SignInScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
            o.ClientId = uiConfig.Auth.GitHub.ClientId;
            o.ClientSecret = uiConfig.Auth.GitHub.ClientSecret;
            o.AuthorizationEndpoint = "https://github.com/login/oauth/authorize";
            o.TokenEndpoint = "https://github.com/login/oauth/access_token";
            o.UserInformationEndpoint = "https://api.github.com/user";
            o.CallbackPath = "/auth/callback/github";
            o.Events.OnCreatingTicket = async ctx =>
            {
                // GitHub-User holen und in einen Naudit-Account materialisieren (Self-Service ⇒ pending).
                using var req = new HttpRequestMessage(HttpMethod.Get, ctx.Options.UserInformationEndpoint);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ctx.AccessToken);
                req.Headers.UserAgent.ParseAdd("Naudit");
                using var res = await ctx.Backchannel.SendAsync(req, ctx.HttpContext.RequestAborted);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ctx.HttpContext.RequestAborted));
                var login = doc.RootElement.GetProperty("login").GetString()!;
                var externalId = doc.RootElement.GetProperty("id").GetInt64().ToString();

                var accounts = ctx.HttpContext.RequestServices.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>();
                var acct = await accounts.MaterializeExternalAsync(
                    Naudit.Infrastructure.Data.AccountProvider.GitHub, externalId, login, login, ctx.HttpContext.RequestAborted);
                ctx.Principal = Naudit.Web.Endpoints.AuthEndpoints.BuildPrincipal(acct); // eigene Claims statt GitHub-Claims
            };
        });
    }
    if (uiConfig.Auth.Oidc.Enabled)
    {
        auth.AddOpenIdConnect("Oidc", o =>
        {
            o.SignInScheme = Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme;
            o.Authority = uiConfig.Auth.Oidc.Authority;
            o.ClientId = uiConfig.Auth.Oidc.ClientId;
            o.ClientSecret = uiConfig.Auth.Oidc.ClientSecret;
            o.ResponseType = "code";
            o.CallbackPath = "/auth/callback/oidc";
            o.GetClaimsFromUserInfoEndpoint = true;
            o.SaveTokens = false;
            o.Events = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectEvents
            {
                OnTicketReceived = async ctx =>
                {
                    // sub (NameIdentifier) = stabile, opake ExternalId. Ohne sub KEINE kollisionssichere
                    // Identität — dann Anmeldung ablehnen, statt auf den mutablen Username auszuweichen
                    // (zwei IdP-Nutzer mit gleichem preferred_username teilten sonst einen Account).
                    var externalId = ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (string.IsNullOrEmpty(externalId))
                    {
                        ctx.Fail("OIDC-Token ohne 'sub' (NameIdentifier)-Claim — Anmeldung abgelehnt.");
                        return;
                    }
                    // Keycloak: preferred_username; Fallbacks für andere IdPs. Nur Anzeigename, nie Identität.
                    var username = ctx.Principal?.FindFirst("preferred_username")?.Value
                        ?? ctx.Principal?.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value
                        ?? externalId;

                    var accounts = ctx.HttpContext.RequestServices.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>();
                    var acct = await accounts.MaterializeExternalAsync(
                        Naudit.Infrastructure.Data.AccountProvider.Oidc, externalId, username, gitHubLogin: null,
                        ctx.HttpContext.RequestAborted);
                    ctx.Principal = Naudit.Web.Endpoints.AuthEndpoints.BuildPrincipal(acct);
                },
            };
        });
    }
}

var app = builder.Build();

// Reverse-Proxy: Coolify/Traefik (und nginx) terminieren TLS und reichen plain HTTP weiter.
// X-Forwarded-Proto übernehmen, damit Request.Scheme wieder "https" ist — sonst baut der
// OAuth-/OIDC-Handler die redirect_uri mit http:// statt https:// und GitHub/der IdP lehnt sie
// als Mismatch zur registrierten Callback-URL ab (Login schlägt fehl).
// Vertrauensgrenze: Standard = leere Listen ⇒ allen Quellen vertrauen (Container-Deployment mit
// dynamischer Proxy-IP; hält das Setup ohne Konfiguration lauffähig). Wer Proxy-IP/-Netz kennt,
// härtet über Naudit:ForwardedHeaders:KnownProxies (IPs) / :KnownNetworks (CIDR) — dann werden
// X-Forwarded-* nur von dort akzeptiert (gegen Spoofing durch direkt erreichbare Clients).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
var fhSection = builder.Configuration.GetSection("Naudit:ForwardedHeaders");
forwardedHeaders.KnownIPNetworks.Clear(); // Loopback-Defaults raus — der Proxy ist nie Loopback.
forwardedHeaders.KnownProxies.Clear();
foreach (var ip in fhSection.GetSection("KnownProxies").Get<string[]>() ?? [])
    forwardedHeaders.KnownProxies.Add(System.Net.IPAddress.Parse(ip));
foreach (var cidr in fhSection.GetSection("KnownNetworks").Get<string[]>() ?? [])
    forwardedHeaders.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
app.UseForwardedHeaders(forwardedHeaders);

// Persistenz: Migration immer, wenn die DB an ist; Seed-Admin nur mit UI (Accounts = UI-Belang).
var dbOptions = app.Services.GetRequiredService<Naudit.Infrastructure.Data.DatabaseOptions>();
if (dbOptions.Enabled)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Data.NauditDbContext>();
    await db.Database.MigrateAsync(); // async im async-Startup — kein Thread-Pool-Blocking
    if (uiConfig.Enabled)
        await scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>().SeedAsync();
}

if (uiConfig.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

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

        // Zugangsschranke: Projekte ohne freigeschalteten Account werden still verworfen
        // (200 nach außen, damit GitHub den Hook nicht deaktiviert; Details nur im Log).
        var gate = context.RequestServices.GetRequiredService<Naudit.Core.Abstractions.IAccessGate>();
        if (!await gate.IsAllowedAsync(request.ProjectId, context.RequestAborted))
        {
            app.Logger.LogInformation("Webhook für nicht freigeschaltetes Projekt {ProjectId} verworfen.", request.ProjectId);
            return Results.Ok();
        }

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

        // Zugangsschranke wie beim GitHub-Hook: still verwerfen, Details nur im Log.
        var gate = context.RequestServices.GetRequiredService<Naudit.Core.Abstractions.IAccessGate>();
        if (!await gate.IsAllowedAsync(request.ProjectId, context.RequestAborted))
        {
            app.Logger.LogInformation("Webhook für nicht freigeschaltetes Projekt {ProjectId} verworfen.", request.ProjectId);
            return Results.Ok();
        }

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

    // Gate auch hier: der CI-Aufrufer ist der Operator selbst ⇒ ehrliches 403 statt Silent-Drop.
    var gate = context.RequestServices.GetRequiredService<Naudit.Core.Abstractions.IAccessGate>();
    if (!await gate.IsAllowedAsync(body.ProjectId, ct))
        return Results.Json(new { error = "project not authorized" }, statusCode: StatusCodes.Status403Forbidden);

    // ReviewService erst nach bestandener Auth auflösen (Scope-Service, inline statt Queue).
    var reviewService = context.RequestServices.GetRequiredService<ReviewService>();
    var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty);
    var result = await reviewService.ReviewAsync(request, ct);

    var verdict = result.Verdict == ReviewVerdict.RequestChanges ? "request_changes" : "approve";
    return Results.Ok(new { verdict });
});

// WebUI-Endpoints nur bei aktiviertem UI mappen (aus = heutiges Verhalten, alles 404).
if (uiConfig.Enabled)
{
    app.MapAuthEndpoints(uiConfig);
    app.MapAdminEndpoints();
    app.MapDataEndpoints();

    // SPA: index.html + Assets aus wwwroot (im Container aus src/frontend gebaut).
    // Fallback-Reihenfolge: echte Endpoints > /api-404 (nie HTML für API-Tippfehler) > index.html.
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
}

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
