using Microsoft.EntityFrameworkCore;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Architektur-Profil-API: destillierte Guidelines je Projekt einsehen, kuratieren,
/// Neu-Destillieren anstoßen. Sichtbarkeit wie das Dashboard, 401/403 statt Redirects.</summary>
public static class GuidelinesEndpoints
{
    private sealed record PutBody(string? Markdown);

    public static void MapGuidelinesEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/projects/{id:int}/guidelines", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            return Results.Ok(new
            {
                markdown = g?.Markdown,
                distilledAt = g?.DistilledAt,
                manuallyEdited = g?.ManuallyEdited ?? false,
                sourcesChangedAt = g?.SourcesChangedAt,
                updatedBy = g?.UpdatedBy,
            });
        });

        api.MapPut("/projects/{id:int}/guidelines", async (HttpContext ctx, NauditDbContext db, int id, PutBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            // Recovery-sicher: ReviewOptions kommt aus AddNauditInfrastructure und fehlt im Recovery-Modus.
            var cap = (ctx.RequestServices.GetService<ReviewOptions>() ?? new ReviewOptions()).Guidelines.MaxProfileChars;
            var markdown = body.Markdown?.Trim();
            if (string.IsNullOrEmpty(markdown))
                return Results.BadRequest(new { error = "markdown must not be empty" });
            if (markdown.Length > cap)
                return Results.BadRequest(new { error = $"markdown must not exceed {cap} characters" });
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            if (g is null)
            {
                g = new ProjectGuidelinesEntity
                {
                    ProjectId = id, Markdown = markdown, SourceHash = "",
                    DistilledAt = DateTime.UtcNow, UpdatedBy = acct.Username,
                };
                db.ProjectGuidelines.Add(g);
            }
            else
            {
                g.Markdown = markdown;
                g.UpdatedBy = acct.Username;
            }
            g.ManuallyEdited = true;          // Kuration gewinnt: Auto-Refresh stoppt ab jetzt
            g.SourcesChangedAt = null;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { manuallyEdited = true });
        });

        api.MapPost("/projects/{id:int}/guidelines/redistill", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            // Kein Inline-LLM-Call (die WebUI hat keinen Checkout): Flags zurücksetzen — der
            // geleerte Hash matcht nie, das nächste Review destilliert frisch.
            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            if (g is not null)
            {
                g.ManuallyEdited = false;
                g.SourceHash = "";
                g.SourcesChangedAt = null;
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            return Results.Ok(new { pending = true });
        });
    }
}
