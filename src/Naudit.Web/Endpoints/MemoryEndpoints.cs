using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Projekt-Gedächtnis-API: FP-Markierung am Finding + Konventionen-Verwaltung.
/// Sichtbarkeit wie das Dashboard (eigene Projekte bzw. Admin), 401/403 statt Redirects.</summary>
public static class MemoryEndpoints
{
    private sealed record FpBody(string? Reason);

    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id, FpBody? body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            // Idempotent: der Eintrag zum selben Finding wird reaktiviert/aktualisiert, nie dupliziert.
            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == id, ctx.RequestAborted);
            if (entry is null)
            {
                entry = new MemoryEntryEntity
                {
                    ProjectId = finding.Review.ProjectId,
                    Kind = "FalsePositive",
                    File = finding.File,
                    Text = finding.Text,
                    SourceFindingId = id,
                    CreatedBy = acct.Username,
                    CreatedAt = DateTime.UtcNow,
                    Active = true,
                };
                db.MemoryEntries.Add(entry);
            }
            entry.Active = true;
            if (!string.IsNullOrWhiteSpace(body?.Reason))
                entry.Reason = body!.Reason!.Trim();
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });

        // Undo (Fehlklick): deaktivieren statt löschen — idempotent, kein Eintrag ⇒ trotzdem 204.
        api.MapDelete("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == id, ctx.RequestAborted);
            if (entry is not null)
            {
                if (!await CurrentAccount.CanSeeProjectAsync(db, acct, entry.ProjectId, ctx.RequestAborted))
                    return Results.Forbid();
                entry.Active = false;
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            return Results.NoContent();
        });
    }
}
