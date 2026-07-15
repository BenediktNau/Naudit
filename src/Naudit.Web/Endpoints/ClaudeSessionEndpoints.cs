using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Selbstverwaltung der Autor-Session (Claude-Code-OAuth-Token) des eingeloggten Accounts.
/// Immer gemappt: GET/PUT/DELETE brauchen nur DB + Data Protection (auch Setup-/Recovery-Modus);
/// der Test-Lauf braucht die Review-Pipeline-Dienste und degradiert sonst auf 503.</summary>
public static class ClaudeSessionEndpoints
{
    public sealed record ClaudeSessionUpdate(string? Token, string? GitAuthorLogin, bool? ShareInPool);

    public static void MapClaudeSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/me/claude-session").RequireAuthorization();

        group.MapGet("", async (HttpContext ctx, NauditDbContext db) =>
        {
            var acct = await CurrentAccount.GetAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            // Registry kommt aus AddNauditInfrastructure — im Setup-/Recovery-Modus nicht da ⇒ kein Cooldown-Status.
            var health = ctx.RequestServices.GetService<SessionHealthRegistry>();
            return Results.Ok(new
            {
                configured = acct.ClaudeSessionToken is not null,
                updatedAtUtc = acct.ClaudeSessionUpdatedAtUtc,
                coolingDownUntil = health?.CoolingDownUntil(acct.Id),
                gitAuthorLogin = acct.GitAuthorLogin,
                shareInPool = acct.ShareSessionInPool,
            });
        });

        group.MapPut("", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions, ClaudeSessionUpdate body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            // Pool-Opt-in ist unabhängig vom Token — zuerst anwenden.
            if (body.ShareInPool is bool share)
                await sessions.SetShareInPoolAsync(acct.Id, share, ctx.RequestAborted);

            // Blank-Semantik wie Settings-Secrets: leerer Token lässt den gespeicherten unangetastet
            // (erlaubt reines Ändern des Logins); ein Erst-PUT ohne Token ist ein Fehler.
            if (string.IsNullOrWhiteSpace(body.Token))
            {
                if (acct.ClaudeSessionToken is null)
                {
                    // Opt-in wurde oben bereits angewendet ⇒ erfolgreich quittieren, kein Token verlangt
                    // (ein Login ohne Token bleibt bewusst ungesetzt — Login braucht einen Token).
                    // „token required" nur, wenn KEIN Opt-in dabei war (reiner Token/Login-Erstversuch).
                    if (body.ShareInPool is not null)
                        return Results.NoContent();
                    return Results.BadRequest(new { error = "token required" });
                }
                await sessions.SetLoginAsync(acct.Id, body.GitAuthorLogin, ctx.RequestAborted);
                return Results.NoContent();
            }

            await sessions.SetTokenAsync(acct.Id, body.Token, body.GitAuthorLogin, ctx.RequestAborted);
            return Results.NoContent();
        });

        group.MapDelete("", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions) =>
        {
            var acct = await CurrentAccount.GetAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();
            await sessions.RemoveTokenAsync(acct.Id, ctx.RequestAborted);
            return Results.NoContent();
        });

        group.MapPost("/test", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            var runner = ctx.RequestServices.GetService<IProcessRunner>();
            var options = ctx.RequestServices.GetService<AuthorSessionsOptions>();
            if (runner is null || options is null)
                return Results.Json(new { ok = false, error = "review pipeline not available (setup/recovery mode)" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var token = sessions.DecryptToken(acct);
            if (token is null)
                return Results.BadRequest(new { error = "no token configured" });

            try
            {
                // Mini-Lauf mit dem hinterlegten Token — gleiche Semantik wie der Test-AI-Schritt im Setup-Wizard.
                var client = new ClaudeCodeChatClient(new AiOptions
                {
                    Provider = AiProvider.ClaudeCode,
                    Model = options.Model,
                    ApiKey = token,
                    TimeoutSeconds = 60,
                }, runner);
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, "Antworte exakt mit: OK"), new ChatMessage(ChatRole.User, "ping")],
                    cancellationToken: ctx.RequestAborted);
                return Results.Ok(new { ok = !string.IsNullOrWhiteSpace(response.Text), error = (string?)null });
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                // Fehlschlag ist ein 200-Ergebnis: das SPA zeigt die Meldung inline an.
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });
    }
}
