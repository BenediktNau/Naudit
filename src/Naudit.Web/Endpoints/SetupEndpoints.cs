using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
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
            draft = draft with { GitToken = null, AiApiKey = null },
            hasGitToken = !string.IsNullOrEmpty(draft.GitToken),
            hasAiApiKey = !string.IsNullOrEmpty(draft.AiApiKey),
        });
    }
}

/// <summary>Request-Body der Admin-Anlage (Wizard Schritt 1).</summary>
public sealed record SetupAdminRequest(string Username, string Password);

/// <summary>Wizard-Zwischenstand: API-Kontrakt UND (serialisiert) der DP-verschluesselte
/// DB-Blob. Alle Felder optional — der Wizard fuellt sie schrittweise. GitToken ist je nach
/// Plattform der GitHub-PAT oder der GitLab-Token (api-Scope).</summary>
public sealed record SetupDraft(
    string? PublicBaseUrl = null,
    string? Platform = null,          // "GitHub" | "GitLab"
    string? GitToken = null,          // Secret: write-only ueber die API
    string? GitLabBaseUrl = null,
    string? WebhookSecret = null,     // von Naudit generiert — bewusst sichtbar/kopierbar
    string? AiProvider = null,        // "Ollama" | "Anthropic" | "OpenAICompatible" | "ClaudeCode"
    string? AiModel = null,
    string? AiEndpoint = null,
    string? AiApiKey = null,          // Secret: write-only ueber die API
    string? AccessGateMode = null);   // "Open" | "Registered"
