using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Auswertungs-API: Acceptance-/FP-Rate, Severity-Breakdown, ISO-Wochen-Trend, Gedächtnis-Wirkung.
/// Sichtbarkeit wie das Dashboard; nur lesend (unabhängig vom Resolution-Schalter).</summary>
public static class AnalyticsEndpoints
{
    private static readonly int[] AllowedDays = [7, 30, 90];

    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/analytics", async (HttpContext ctx, NauditDbContext db, int? projectId, int days) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (days == 0) days = 30;
            if (!AllowedDays.Contains(days))
                return Results.BadRequest(new { error = "days must be 7, 30 or 90" });

            var projectsQuery = CurrentAccount.VisibleProjects(db, acct);
            if (projectId is int pid)
            {
                if (!await projectsQuery.AnyAsync(p => p.Id == pid, ctx.RequestAborted)) return Results.Forbid();
                projectsQuery = projectsQuery.Where(p => p.Id == pid);
            }
            var projects = await projectsQuery
                .Include(p => p.Reviews).ThenInclude(r => r.Findings)
                .ToListAsync(ctx.RequestAborted);

            var since = DateTime.UtcNow.Date.AddDays(-days + 1);
            var findings = projects
                .SelectMany(p => p.Reviews)
                .Where(r => r.CreatedAt.Date >= since)
                .SelectMany(r => r.Findings.Select(f => (r.CreatedAt, f.Severity, f.ResolutionStatus)))
                .ToList();

            int posted = findings.Count;
            int accepted = findings.Count(f => f.ResolutionStatus == "Accepted");
            int rejected = findings.Count(f => f.ResolutionStatus == "Rejected");
            int unanswered = posted - accepted - rejected;
            double Rate(int n) => posted == 0 ? 0 : (double)n / posted;

            string[] severities = ["critical", "high", "medium", "low", "info"];
            var bySeverity = severities.Select(s =>
            {
                var g = findings.Where(f => string.Equals(f.Severity, s, StringComparison.OrdinalIgnoreCase)).ToList();
                return new
                {
                    severity = s,
                    posted = g.Count,
                    accepted = g.Count(f => f.ResolutionStatus == "Accepted"),
                    rejected = g.Count(f => f.ResolutionStatus == "Rejected"),
                };
            }).Where(x => x.posted > 0);

            var weekly = findings
                .GroupBy(f => IsoWeekStart(f.CreatedAt))
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    weekStart = g.Key.ToString("yyyy-MM-dd"),
                    posted = g.Count(),
                    accepted = g.Count(f => f.ResolutionStatus == "Accepted"),
                    rejected = g.Count(f => f.ResolutionStatus == "Rejected"),
                });

            var projectIds = projects.Select(p => p.Id).ToList();
            var memoryEntries = await db.MemoryEntries.Where(m => projectIds.Contains(m.ProjectId)).ToListAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                totals = new
                {
                    posted, accepted, rejected, unanswered,
                    acceptanceRate = Rate(accepted), fpRate = Rate(rejected),
                },
                bySeverity,
                weekly,
                memory = new
                {
                    entries = memoryEntries.Count,
                    active = memoryEntries.Count(m => m.Active),
                    timesApplied = memoryEntries.Sum(m => m.TimesApplied),
                },
            });
        });
    }

    // ISO-8601-Wochenstart (Montag) des Datums — provider-neutral in-memory.
    private static DateTime IsoWeekStart(DateTime dt)
    {
        var d = dt.Date;
        int diff = ((int)d.DayOfWeek + 6) % 7;   // Montag=0 … Sonntag=6
        return d.AddDays(-diff);
    }
}
