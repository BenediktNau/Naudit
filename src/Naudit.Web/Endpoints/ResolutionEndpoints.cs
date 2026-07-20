using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Analytics;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Resolution-API: Finding im Review-Detail als Accepted/Rejected markieren (Quelle WebUi).
/// Sichtbarkeit wie das Dashboard, 401/403 statt Redirects.</summary>
public static class ResolutionEndpoints
{
    private sealed record ResolutionBody(string? Status);
    private static readonly string[] Valid = [ResolutionValues.Accepted, ResolutionValues.Rejected];

    public static void MapResolutionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPut("/findings/{id:int}/resolution", async (HttpContext ctx, NauditDbContext db, int id, ResolutionBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            // status null = Undo; sonst muss er gültig sein.
            if (body.Status is not null && !Valid.Contains(body.Status))
                return Results.BadRequest(new { error = "status must be Accepted, Rejected or null" });

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            await ResolutionWriter.ApplyAsync(db, finding, body.Status, ResolutionValues.Sources.WebUi, acct.Username, ctx.RequestAborted);
            return Results.Ok(new { id = finding.Id, resolutionStatus = finding.ResolutionStatus });
        });
    }
}
