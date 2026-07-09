using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Setup;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Wizard-API. Der Status-Endpoint ist IMMER gemappt (das SPA entscheidet damit
/// Wizard vs. App und pollt ihn nach dem Uebernehmen-Neustart); alle anderen Endpoints
/// existieren nur im Setup-Modus. Schutz nach dem Grafana-Muster: Admin anlegen geht nur,
/// solange KEIN Admin existiert — danach ist Login Pflicht.</summary>
public static class SetupEndpoints
{
    public static void MapSetupEndpoints(this WebApplication app, SetupStatusResult setup)
    {
        app.MapGet("/api/setup/status", async (HttpContext ctx, NauditDbContext db) =>
        {
            var adminExists = await db.Accounts.AnyAsync(a => a.IsAdmin, ctx.RequestAborted);
            return Results.Ok(new
            {
                setupRequired = setup.SetupRequired,
                adminExists,
                missing = setup.MissingKeys,
                // Aus dem Request abgeleitet (ForwardedHeaders sind bereits verarbeitet) — Vorbelegung fuer Schritt 2.
                suggestedPublicBaseUrl = setup.SetupRequired ? $"{ctx.Request.Scheme}://{ctx.Request.Host}" : null,
            });
        });

        if (!setup.SetupRequired) return; // konfiguriert ⇒ keine Wizard-Flaeche

        app.MapPost("/api/setup/admin", async (SetupAdminRequest body, AccountService accounts,
            NauditDbContext db, HttpContext ctx) =>
        {
            if (await db.Accounts.AnyAsync(a => a.IsAdmin, ctx.RequestAborted))
                return Results.Conflict(new { error = "An admin account already exists — sign in instead." });
            AccountEntity acct;
            try
            {
                acct = await accounts.CreateLocalAsync(body.Username, body.Password, isAdmin: true, [], ctx.RequestAborted);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            // Direkt einloggen — die weiteren Wizard-Schritte sind RequireAuthorization + Admin-Check.
            await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, AuthEndpoints.BuildPrincipal(acct));
            return Results.Ok(new { username = acct.Username });
        });

        // Browser-Redirect von GitHub — bewusst OHNE Cookie-Pflicht: Credential ist der
        // unratbare, an den Draft gebundene, einmal verwendbare state (CSRF). So bricht der
        // Flow nicht, wenn der Cookie den externen Redirect nicht ueberlebt. Antwort ist
        // immer ein Redirect auf die SPA, nie JSON.
        app.MapGet("/api/setup/github/manifest-callback", async (string? code, string? state,
            HttpContext ctx, SetupDraftService drafts, SetupHttpClientFactory httpFactory) =>
        {
            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var draft = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json);
            if (draft is null || string.IsNullOrEmpty(code) || !StateMatches(draft.GitHubManifestState, state))
                return Results.Redirect("/?setup=github-app-error&reason=state");

            try
            {
                using var http = httpFactory.Create();
                var conversion = await new Naudit.Infrastructure.Setup.GitHubManifestConverter(http)
                    .ConvertAsync(draft.GitHubHost, code, ctx.RequestAborted);
                var updated = draft with
                {
                    GitHubAppId = conversion.AppId,
                    GitHubAppPrivateKey = conversion.PrivateKey,
                    WebhookSecret = conversion.WebhookSecret, // GitHubs generiertes Secret ersetzt unseres
                    GitHubAppSlug = conversion.Slug,
                    GitHubManifestState = null,               // einmal verwendbar
                };
                await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(updated), ctx.RequestAborted);
                return Results.Redirect("/?setup=github-app-created");
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
            {
                // Draft + state bleiben — Fehler im Schritt, erneut versuchbar (Spec).
                return Results.Redirect("/?setup=github-app-error&reason=conversion");
            }
        });

        var group = app.MapGroup("/api/setup").RequireAuthorization();

        group.MapGet("/draft", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts) =>
            await DraftResponseAsync(ctx, db, drafts));

        group.MapPut("/draft", async (SetupDraft incoming, HttpContext ctx, NauditDbContext db,
            SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            // Merge-Semantik: GET maskiert Secrets, das SPA kann sie nicht zurueckschicken —
            // leere Secret-Felder heissen "gespeicherten Wert behalten". Ausnahme Plattformwechsel:
            // ein GitHub-PAT taugt nicht fuer GitLab (und umgekehrt) ⇒ Token verfaellt.
            var existingJson = await drafts.LoadAsync(ctx.RequestAborted);
            var existing = existingJson is null
                ? new SetupDraft()
                : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(existingJson)!;
            var samePlatform = string.Equals(incoming.Platform, existing.Platform, StringComparison.OrdinalIgnoreCase);
            var merged = incoming with
            {
                GitToken = !string.IsNullOrEmpty(incoming.GitToken)
                    ? incoming.GitToken : (samePlatform ? existing.GitToken : null),
                AiApiKey = !string.IsNullOrEmpty(incoming.AiApiKey) ? incoming.AiApiKey : existing.AiApiKey,
                // Serverseitig verwaltet (Manifest-Callback): PUT ignoriert eingehende Werte komplett.
                GitHubAppId = samePlatform ? existing.GitHubAppId : null,
                GitHubAppPrivateKey = samePlatform ? existing.GitHubAppPrivateKey : null,
                GitHubAppSlug = samePlatform ? existing.GitHubAppSlug : null,
                GitHubManifestState = samePlatform ? existing.GitHubManifestState : null,
            };
            await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(merged), ctx.RequestAborted);
            return Results.Ok(new { saved = true });
        });

        group.MapDelete("/draft", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            await drafts.ClearAsync(ctx.RequestAborted);
            return Results.NoContent();
        });

        group.MapPost("/test-ai", async (AiTestRequest body, HttpContext ctx, NauditDbContext db,
            SetupDraftService drafts, AiTestClientFactory factory) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            if (!Enum.TryParse<Naudit.Infrastructure.Ai.AiProvider>(body.Provider, true, out var provider))
                return Results.BadRequest(new { error = $"Unknown AI provider '{body.Provider}'." });

            // ApiKey ist im SPA maskiert, wenn er aus dem Draft stammt — leer ⇒ Draft-Wert nehmen.
            var apiKey = body.ApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                var json = await drafts.LoadAsync(ctx.RequestAborted);
                apiKey = json is null ? null
                    : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!.AiApiKey;
            }

            var options = new Naudit.Infrastructure.Ai.AiOptions
            {
                Provider = provider,
                Model = body.Model ?? "",
                Endpoint = string.IsNullOrWhiteSpace(body.Endpoint) ? null : body.Endpoint,
                ApiKey = apiKey,
                TimeoutSeconds = 30, // Verbindungstest, kein Review — kurz halten
            };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                var client = factory.Create(options);
                try
                {
                    var response = await client.GetResponseAsync("Reply with the single word: OK", cancellationToken: cts.Token);
                    var text = response.Text;
                    return Results.Ok(new { ok = true, detail = text.Length > 200 ? text[..200] : text });
                }
                finally
                {
                    (client as IDisposable)?.Dispose();
                }
            }
            catch (Exception ex)
            {
                // Scheitern ist hier ein ERGEBNIS (Spec: Fortfahren erlaubt, z. B. Ollama noch nicht erreichbar).
                return Results.Ok(new { ok = false, detail = ex.Message });
            }
        });

        group.MapPost("/github/manifest", async (GitHubManifestRequest body, HttpContext ctx,
            NauditDbContext db, SetupDraftService drafts) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var existing = json is null ? new SetupDraft()
                : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;
            if (string.IsNullOrWhiteSpace(existing.PublicBaseUrl))
                return Results.BadRequest(new
                { error = "Set the instance URL first — the manifest needs it for the webhook and redirect URLs." });

            // state + host in den Draft: der Callback validiert dagegen und braucht den Host
            // fuer die API-Base (GHES). Plattform-Wahl gleich mit persistieren.
            var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            var host = Naudit.Infrastructure.Setup.GitHubManifest.Normalize(body.GitHubHost);
            var updated = existing with
            {
                Platform = "GitHub",
                GitHubAuth = "App",
                GitHubHost = host,
                GitHubManifestState = state,
            };
            await drafts.SaveAsync(System.Text.Json.JsonSerializer.Serialize(updated), ctx.RequestAborted);

            var appName = string.IsNullOrWhiteSpace(body.AppName) ? "naudit" : body.AppName.Trim();
            return Results.Ok(new
            {
                action = Naudit.Infrastructure.Setup.GitHubManifest.CreateAppUrl(host, body.Org, state),
                manifest = Naudit.Infrastructure.Setup.GitHubManifest.Build(
                    existing.PublicBaseUrl, appName, body.Public),
            });
        });

        group.MapPost("/gitlab/hooks", async (GitLabHooksRequest body, HttpContext ctx,
            NauditDbContext db, SetupDraftService drafts, SetupHttpClientFactory httpFactory) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            var json = await drafts.LoadAsync(ctx.RequestAborted);
            var draft = json is null ? null : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json);
            if (draft is null || string.IsNullOrWhiteSpace(draft.GitLabBaseUrl)
                || string.IsNullOrWhiteSpace(draft.GitToken) || string.IsNullOrWhiteSpace(draft.WebhookSecret)
                || string.IsNullOrWhiteSpace(draft.PublicBaseUrl))
                return Results.BadRequest(new
                { error = "Complete the instance URL and GitLab fields first (base URL, token, webhook secret)." });

            var targets = new List<Naudit.Infrastructure.Setup.GitLabHookTarget>();
            foreach (var p in body.Projects ?? [])
                if (!string.IsNullOrWhiteSpace(p))
                    targets.Add(new(Naudit.Infrastructure.Setup.GitLabHookTargetKind.Project, p.Trim()));
            foreach (var g in body.Groups ?? [])
                if (!string.IsNullOrWhiteSpace(g))
                    targets.Add(new(Naudit.Infrastructure.Setup.GitLabHookTargetKind.Group, g.Trim()));
            if (targets.Count == 0)
                return Results.BadRequest(new { error = "Enter at least one project or group." });

            var webhookUrl = $"{draft.PublicBaseUrl.TrimEnd('/')}/webhook/gitlab";
            using var http = httpFactory.Create();
            var results = await new Naudit.Infrastructure.Setup.GitLabHookCreator(http).CreateAsync(
                draft.GitLabBaseUrl, draft.GitToken, webhookUrl, draft.WebhookSecret, targets, ctx.RequestAborted);
            return Results.Ok(new
            {
                results = results.Select(r => new
                {
                    target = r.Target.IdOrPath,
                    kind = r.Target.Kind.ToString().ToLowerInvariant(),
                    ok = r.Ok,
                    status = r.Status,
                    detail = r.Detail,
                }),
            });
        });

        group.MapPost("/apply", async (HttpContext ctx, NauditDbContext db, SetupDraftService drafts,
            Naudit.Infrastructure.Settings.SettingsService settings,
            Naudit.Infrastructure.Settings.EnvOverrides env, IAppRestarter restarter) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            var json = await drafts.LoadAsync(ctx.RequestAborted);
            if (json is null)
                return Results.BadRequest(new { error = "No setup draft to apply.", missing = Array.Empty<string>() });
            var draft = System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;

            // Validierung = dieselbe Pflichtset-Logik wie beim Start: Draft-Werte unten,
            // env-Overrides oben (env gewinnt und zaehlt als erfuellt).
            var values = DraftToSettings(draft);
            var effective = new ConfigurationBuilder()
                .AddInMemoryCollection(values)
                .AddConfiguration(env.Root)
                .Build();
            var check = SetupStatus.Check(effective);
            if (check.SetupRequired)
                return Results.BadRequest(new { error = "Setup is incomplete.", missing = check.MissingKeys });
            if (string.IsNullOrWhiteSpace(effective["Naudit:PublicBaseUrl"]))
                return Results.BadRequest(new { error = "Setup is incomplete.", missing = new[] { "Naudit:PublicBaseUrl" } });

            // Atomar uebernehmen: alle Werte + Draft-Loeschung in EINER Transaktion.
            await using var tx = await db.Database.BeginTransactionAsync(ctx.RequestAborted);
            foreach (var (key, value) in values)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;      // nicht gesetzt ⇒ nichts schreiben
                if (env.Root[key] is not null) continue;             // env gewinnt ⇒ DB nicht anfassen
                await settings.SetAsync(key, value, ctx.RequestAborted);
            }
            await drafts.ClearAsync(ctx.RequestAborted);
            await tx.CommitAsync(ctx.RequestAborted);

            restarter.RequestRestart(); // Host-Schleife baut neu — die Settings greifen danach
            return Results.Ok(new { restarting = true });
        });
    }

    /// <summary>GET-Antwort des Drafts — Secrets (GitToken/AiApiKey) werden NIE zurueckgegeben,
    /// nur has*-Flags. Das selbst generierte WebhookSecret ist bewusst sichtbar (Copy-Paste
    /// in die Plattform-Oberflaeche). Von Task 5 (PUT/DELETE) mitverwendet.</summary>
    internal static async Task<IResult> DraftResponseAsync(HttpContext ctx, NauditDbContext db, SetupDraftService drafts)
    {
        if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
        var json = await drafts.LoadAsync(ctx.RequestAborted);
        var draft = json is null ? new SetupDraft() : System.Text.Json.JsonSerializer.Deserialize<SetupDraft>(json)!;
        return Results.Ok(new
        {
            draft = draft with
            {
                GitToken = null, AiApiKey = null,
                GitHubAppPrivateKey = null, GitHubManifestState = null,
            },
            hasGitToken = !string.IsNullOrEmpty(draft.GitToken),
            hasAiApiKey = !string.IsNullOrEmpty(draft.AiApiKey),
            hasGitHubApp = !string.IsNullOrEmpty(draft.GitHubAppId)
                && !string.IsNullOrEmpty(draft.GitHubAppPrivateKey),
        });
    }

    /// <summary>Constant-time-Vergleich des CSRF-states (Laengen-Differenz ⇒ false).</summary>
    private static bool StateMatches(string? expected, string? actual)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return false;
        var e = System.Text.Encoding.UTF8.GetBytes(expected);
        var a = System.Text.Encoding.UTF8.GetBytes(actual);
        return e.Length == a.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(e, a);
    }

    /// <summary>Draft → Setting-Keys. Nur die zur gewaehlten Plattform gehoerenden Keys —
    /// Reste der anderen Plattform landen nie in der DB.</summary>
    private static Dictionary<string, string?> DraftToSettings(SetupDraft d)
    {
        var values = new Dictionary<string, string?>
        {
            ["Naudit:PublicBaseUrl"] = d.PublicBaseUrl,
            ["Naudit:Git:Platform"] = d.Platform,
            ["Naudit:Ai:Provider"] = d.AiProvider,
            ["Naudit:Ai:Model"] = d.AiModel,
            ["Naudit:Ai:Endpoint"] = d.AiEndpoint,
            ["Naudit:Ai:ApiKey"] = d.AiApiKey,
            ["Naudit:AccessGate:Mode"] = d.AccessGateMode,
        };
        if (string.Equals(d.Platform, "GitHub", StringComparison.OrdinalIgnoreCase))
        {
            var app = string.Equals(d.GitHubAuth, "App", StringComparison.OrdinalIgnoreCase);
            values["Naudit:GitHub:Auth"] = app ? "App" : "Pat";
            if (app)
            {
                values["Naudit:GitHub:App:AppId"] = d.GitHubAppId;
                values["Naudit:GitHub:App:PrivateKey"] = d.GitHubAppPrivateKey;
                // GHES: API-Base persistieren; github.com bleibt beim Options-Default.
                var host = Naudit.Infrastructure.Setup.GitHubManifest.Normalize(d.GitHubHost);
                if (host != Naudit.Infrastructure.Setup.GitHubManifest.DefaultWebHost)
                    values["Naudit:GitHub:BaseUrl"] = Naudit.Infrastructure.Setup.GitHubManifest.ApiBase(host);
            }
            else
            {
                values["Naudit:GitHub:Token"] = d.GitToken;
            }
            values["Naudit:GitHub:WebhookSecret"] = d.WebhookSecret;
        }
        else if (string.Equals(d.Platform, "GitLab", StringComparison.OrdinalIgnoreCase))
        {
            values["Naudit:GitLab:BaseUrl"] = d.GitLabBaseUrl;
            values["Naudit:GitLab:Token"] = d.GitToken;
            values["Naudit:GitLab:WebhookSecret"] = d.WebhookSecret;
        }
        return values;
    }
}

/// <summary>Request-Body der Admin-Anlage (Wizard Schritt 1).</summary>
public sealed record SetupAdminRequest(string Username, string Password);

/// <summary>Request-Body des AI-Verbindungstests; ApiKey leer ⇒ gespeicherter Draft-Wert.</summary>
public sealed record AiTestRequest(string? Provider, string? Model, string? Endpoint, string? ApiKey);

/// <summary>Request des Manifest-Starts. Org/AppName/Public gehen nur an GitHub
/// (nicht persistiert); GitHubHost wandert in den Draft (Callback + Apply brauchen ihn).</summary>
public sealed record GitHubManifestRequest(
    string? GitHubHost = null, string? Org = null, string? AppName = null, bool Public = false);

/// <summary>Ziele der GitLab-Webhook-Anlage: Projekt-IDs/-Pfade und Gruppen-IDs/-Pfade.</summary>
public sealed record GitLabHooksRequest(List<string>? Projects = null, List<string>? Groups = null);

/// <summary>Wizard-Zwischenstand: API-Kontrakt UND (serialisiert) der DP-verschluesselte
/// DB-Blob. Alle Felder optional — der Wizard fuellt sie schrittweise. GitToken ist je nach
/// Plattform der GitHub-PAT oder der GitLab-Token (api-Scope).</summary>
public sealed record SetupDraft(
    string? PublicBaseUrl = null,
    string? Platform = null,          // "GitHub" | "GitLab"
    string? GitToken = null,          // Secret: write-only ueber die API
    string? GitLabBaseUrl = null,
    string? WebhookSecret = null,     // von Naudit generiert bzw. von GitHub (Manifest) — bewusst sichtbar
    string? AiProvider = null,        // "Ollama" | "Anthropic" | "OpenAICompatible" | "ClaudeCode"
    string? AiModel = null,
    string? AiEndpoint = null,
    string? AiApiKey = null,          // Secret: write-only ueber die API
    string? AccessGateMode = null,    // "Open" | "Registered"
    string? GitHubAuth = null,        // "Pat" | "App" — Wizard-Wahl, wie Naudit:GitHub:Auth
    string? GitHubHost = null,        // Web-Host (Default https://github.com; GHES: eigener Host)
    string? GitHubAppId = null,       // ab hier serverseitig: nur der Manifest-Callback schreibt
    string? GitHubAppPrivateKey = null, // Secret: nie in GET, nie per PUT setzbar
    string? GitHubAppSlug = null,     // fuer den Install-Link
    string? GitHubManifestState = null); // CSRF-State des Manifest-Flows — nie in GET
