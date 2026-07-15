using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Infrastructure.Settings;
using Naudit.Web;
using Naudit.Web.Endpoints;

// Host-Schleife: baut den Host, läuft ihn, und baut ihn NEU, wenn ein Endpoint (Task 8) einen
// kontrollierten Neustart angefordert hat. So werden Config-Änderungen aus der DB übernommen,
// ohne den Prozess/Container zu killen. Ohne Restart-Wunsch: ein Durchlauf, dann Ende.
var restarter = new AppRestarter();
while (true)
{
    var app = BuildApp(args, restarter);
    try
    {
        // Seed läuft immer (auch Recovery): der Admin muss sich einloggen können, um zu reparieren.
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>().SeedAsync();
        restarter.Attach(app.Lifetime);
        await app.RunAsync();
    }
    finally
    {
        // Immer entsorgen — auch wenn Seed/Run wirft: sonst leckt pro Neustart ein kompletter
        // DI-Container (DbContext, HttpClients, ...).
        await app.DisposeAsync();
    }
    // Restart-Entscheidung nach dem Dispose lesen: der restarter lebt außerhalb der Schleife,
    // das Entsorgen des Hosts berührt ihn nicht.
    var restartRequested = restarter.ConsumeRestartRequest();
    if (!restartRequested) break;
}

static WebApplication BuildApp(string[] args, AppRestarter restarter)
{
    var builder = WebApplication.CreateBuilder(args);

    // 1) Bootstrap: DB migrieren, Settings laden, als Config-Quelle UNTER Env/User-Secrets einhängen.
    //    CreateBuilder hat appsettings + User-Secrets + Env + CommandLine bereits angehängt — jetzt
    //    InsertDbSettings aufrufen (Vorbedingung „nach den Env-Quellen" erfüllt); danach KEINE weitere
    //    Config-Quelle mehr ergänzen. Precedence: appsettings < DB < User-Secrets/Env/CommandLine.
    var dbOptions = builder.Configuration.GetSection("Naudit:Db").Get<Naudit.Infrastructure.Data.DatabaseOptions>()
        ?? new Naudit.Infrastructure.Data.DatabaseOptions();
    var load = DbSettingsLoader.Load(dbOptions);
    var envOverrides = NauditConfig.InsertDbSettings(builder.Configuration, load.Settings);

    // 1b) Setup-Erkennung: fehlt Pflicht-Config (Token/Secrets/Model je Plattform & Provider),
    //     faehrt der Host im Setup-Modus hoch — Wizard statt Webhooks. FEHLENDE Werte ⇒ Wizard;
    //     UNGUELTIGE Werte (z. B. kaputter Enum) laufen weiter in den Probe ⇒ Recovery-Modus.
    var setup = Naudit.Infrastructure.Setup.SetupStatus.Check(builder.Configuration);

    // 2) Probe: registriert die Review-Infrastruktur in einen WEGWERF-Container. Wirft sie
    //    (z. B. Auth=App ohne PrivateKey), starten wir im Recovery-Modus statt in der Crash-Loop.
    //    Im Setup-Modus entfaellt der Probe — unvollstaendige Config ist dort der Normalfall.
    Exception? configError = null;
    if (!setup.SetupRequired)
    {
        try
        {
            var probe = new ServiceCollection();
            probe.AddLogging();
            probe.AddNauditInfrastructure(builder.Configuration);
        }
        catch (Exception ex) { configError = ex; }
    }
    var reviewActive = !setup.SetupRequired && configError is null;

    // WebUI-Auth (BFF): Cookie-Session; API-Verhalten statt Browser-Redirects (401/403 als Status).
    // GetSection NACH InsertDbSettings lesen, damit DB-Werte greifen.
    var uiConfig = builder.Configuration.GetSection("Naudit:Ui").Get<Naudit.Infrastructure.Ui.UiOptions>()
        ?? new Naudit.Infrastructure.Ui.UiOptions();

    // 3) Basis immer: DB, Auth/Cookies, DataProtection, UI. Review-Teile nur bei gesunder Config.
    builder.Services.AddSingleton<IAppRestarter>(restarter);
    builder.Services.AddSingleton(envOverrides);
    builder.Services.AddSingleton(new StartupState(configError?.Message, load.Warnings));
    builder.Services.AddSingleton(setup);
    builder.Services.AddSingleton(new AiTestClientFactory(Naudit.Infrastructure.Ai.AiClientFactory.Create));
    builder.Services.AddSingleton(new SetupHttpClientFactory(() => new HttpClient()));
    builder.Services.AddNauditDatabase(builder.Configuration);
    // UiOptions gehört zur immer-an UI-Basis (AccountService/Seed brauchen sie schon im Recovery-Modus,
    // wo AddNauditInfrastructure NICHT läuft). Im Gesundfall registriert AddNauditInfrastructure sie erneut
    // (identisch aus derselben Config) — letzte Registrierung gewinnt, harmlos.
    builder.Services.AddSingleton(uiConfig);
    if (reviewActive)
    {
        builder.Services.AddNauditInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
        builder.Services.AddHostedService<ReviewBackgroundService>();
    }

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
    // auf beiden Backends, kein Key-Verzeichnis/Volume nötig. AddNauditDatabase garantiert den DbContext.
    builder.Services.AddDataProtection()
        .PersistKeysToDbContext<Naudit.Infrastructure.Data.NauditDbContext>()
        .SetApplicationName(DbSettingsLoader.DataProtectionAppName);

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

    // Migration passiert im DbSettingsLoader (vor dem Host-Bau) — kein zweiter MigrateAsync-Block hier.
    // Der Seed-Admin läuft in der Host-Schleife (immer, auch im Recovery-Modus).

    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/health", () => Results.Ok("healthy"));

    if (reviewActive)
    {
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
                // Konstant-zeitlicher Vergleich wie beim /review-Endpoint — kein Timing-Leak des Secrets.
                if (!IsValidNauditToken(secret, token))
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
            var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty, body.AuthorLogin);
            var result = await reviewService.ReviewAsync(request, ct);

            var verdict = result.Verdict == ReviewVerdict.RequestChanges ? "request_changes" : "approve";
            return Results.Ok(new { verdict });
        });

        // GitHub-App-Installations-Status fürs Onboarding-Banner — mappt sich selbst nur bei
        // Platform=GitHub & Auth=App. Gehört in den Gesund-Block: GitOptions/GitHubOptions und der
        // Installations-Checker kommen erst aus AddNauditInfrastructure (im Recovery-Modus nicht da).
        app.MapGitHubAppEndpoints(
            app.Services.GetRequiredService<GitOptions>(),
            app.Services.GetRequiredService<IOptions<GitHubOptions>>().Value);
    }
    else if (setup.SetupRequired)
    {
        app.Logger.LogWarning("Setup-Modus: fehlende Pflicht-Konfiguration ({Missing}) — " +
            "Webhooks/Review sind deaktiviert, Einrichtung über den Wizard in der WebUI.",
            string.Join(", ", setup.MissingKeys));
    }
    else
    {
        app.Logger.LogError("Recovery-Modus: {Error} — Webhooks/Review sind deaktiviert, " +
            "Korrektur über die Settings-Seite, dann Neustart.", configError!.Message);
    }
    foreach (var warning in load.Warnings) app.Logger.LogWarning("{Warning}", warning);

    // Obsoleter Schalter (Author-Sessions → SessionRouting-Enum): fällt sonst still auf
    // SessionRouting=Single zurück — der Admin bemerkt das nur an ausbleibenden Autor-Sessions.
    if (!string.IsNullOrEmpty(builder.Configuration["Naudit:Ai:AuthorSessions:Enabled"]))
    {
        app.Logger.LogWarning(
            "Naudit:Ai:AuthorSessions:Enabled ist obsolet und wird ignoriert — " +
            "ersetzt durch Naudit:Ai:SessionRouting (Single|Author|RoundRobin).");
    }

    // WebUI-Endpoints: immer gemappt (die UI ist immer an — sie ist im Recovery-Modus das Reparaturwerkzeug).
    app.MapAuthEndpoints(uiConfig);
    app.MapClaudeSessionEndpoints();
    app.MapAdminEndpoints();
    app.MapDataEndpoints();
    app.MapSettingsEndpoints();
    app.MapSetupEndpoints(setup);

    // SPA: index.html + Assets aus wwwroot (im Container aus src/frontend gebaut).
    // Fallback-Reihenfolge: echte Endpoints > /api-404 (nie HTML für API-Tippfehler) > index.html.
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");

    return app;

    // Konstant-zeitlicher Vergleich; leeres Secret oder leerer Token ⇒ false (fail-closed).
    static bool IsValidNauditToken(string? secret, string? provided)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(provided))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(provided));
    }
}

// Hinweis: In .NET 10 ist die generierte Program-Klasse automatisch public,
// daher von WebApplicationFactory<Program> im Testprojekt direkt nutzbar.

/// <summary>Request-Body des CI-Triggers; wird direkt auf ReviewRequest gemappt
/// (bei GitHub ist ProjectId = "owner/repo" und MergeRequestIid = PR-Nummer).</summary>
public sealed record ReviewTriggerRequest(string ProjectId, int MergeRequestIid, string? Title, string? AuthorLogin = null);
