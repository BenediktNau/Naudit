using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Projekt-Gedächtnis-API: FP-Markierung am Finding + Konventionen-Verwaltung.
/// Sichtbarkeit wie das Dashboard (eigene Projekte bzw. Admin), 401/403 statt Redirects.</summary>
public static class MemoryEndpoints
{
    private sealed record FpBody(string? Reason);
    private sealed record ConventionBody(string? Text, string? File);
    private sealed record ToggleBody(bool Active);

    /// <summary>Deckel für nutzergeschriebene Freitext-Felder (Reason/Text). Diese Einträge werden in
    /// den Prompt JEDES künftigen Reviews gespiegelt — ein sehr großer Eintrag bläht dauerhaft
    /// Token/Latenz. Datei-Scope ist kürzer gedeckelt.</summary>
    private const int MaxFreeTextLength = 4000;
    private const int MaxFileLength = 500;

    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id, FpBody? body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (body?.Reason is { Length: > MaxFreeTextLength })
                return Results.BadRequest(new { error = $"reason must not exceed {MaxFreeTextLength} characters" });

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            var entry = await Naudit.Infrastructure.Memory.MemoryEntryWriter.MarkFalsePositiveAsync(
                db, finding, body?.Reason, acct.Username, ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });

        // Undo (Fehlklick): deaktivieren statt löschen. Autorisierung wie bei POST am FINDING
        // (nicht erst am Eintrag): sonst verriete 403-vs-204 die Existenz eines FP-Eintrags in
        // einem fremden Projekt. Bekanntes Finding + sichtbar ⇒ 204 auch ohne Eintrag (idempotent).
        api.MapDelete("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == id, ctx.RequestAborted);
            if (entry is not null)
            {
                entry.Active = false;
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            return Results.NoContent();
        });

        api.MapGet("/projects/{id:int}/memory", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var entries = await db.MemoryEntries
                .Where(m => m.ProjectId == id)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Select(m => new
                {
                    id = m.Id, kind = m.Kind, file = m.File, text = m.Text, reason = m.Reason,
                    createdBy = m.CreatedBy, createdAt = m.CreatedAt, active = m.Active,
                    sourceFindingId = m.SourceFindingId,
                })
                .ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { entries });
        });

        api.MapPost("/projects/{id:int}/memory", async (HttpContext ctx, NauditDbContext db, int id, ConventionBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "text must not be empty" });
            if (body.Text.Length > MaxFreeTextLength)
                return Results.BadRequest(new { error = $"text must not exceed {MaxFreeTextLength} characters" });
            if (body.File is { Length: > MaxFileLength })
                return Results.BadRequest(new { error = $"file must not exceed {MaxFileLength} characters" });
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var entry = new MemoryEntryEntity
            {
                ProjectId = id,
                Kind = "Convention",
                File = string.IsNullOrWhiteSpace(body.File) ? null : body.File.Trim(),
                Text = body.Text.Trim(),
                CreatedBy = acct.Username,
                CreatedAt = DateTime.UtcNow,
                Active = true,
            };
            db.MemoryEntries.Add(entry);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });

        api.MapPut("/memory/{id:int}", async (HttpContext ctx, NauditDbContext db, int id, ToggleBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.Id == id, ctx.RequestAborted);
            if (entry is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, entry.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            entry.Active = body.Active;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });
    }
}
