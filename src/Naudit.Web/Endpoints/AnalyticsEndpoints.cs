using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Analytics;
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

        api.MapGet("/analytics", async (HttpContext ctx, NauditDbContext db, int? projectId, int? days) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            // "days" ist optional (Default 30) — nullable, sonst behandelt der Minimal-API-Binder
            // ein fehlendes Query-Param als Pflichtfeld (400 statt Default). Ein explizites
            // days=0 ist dagegen ungültig und läuft in die AllowedDays-Prüfung (400).
            var d = days ?? 30;
            if (!AllowedDays.Contains(d))
                return Results.BadRequest(new { error = "days must be 7, 30 or 90" });

            var projectsQuery = CurrentAccount.VisibleProjects(db, acct);
            if (projectId is int pid)
            {
                if (!await projectsQuery.AnyAsync(p => p.Id == pid, ctx.RequestAborted)) return Results.Forbid();
                projectsQuery = projectsQuery.Where(p => p.Id == pid);
            }
            // Datums-Fenster schon in der Query (Filtered Include): begrenzt die geladene Menge
            // auf das Fenster statt der gesamten Review-Historie; "since" ist Mitternacht, damit
            // ist CreatedAt >= since äquivalent zum früheren CreatedAt.Date >= since.
            var since = DateTime.UtcNow.Date.AddDays(-d + 1);
            var projects = await projectsQuery
                .Include(p => p.Reviews.Where(r => r.CreatedAt >= since)).ThenInclude(r => r.Findings)
                .ToListAsync(ctx.RequestAborted);

            var findings = projects
                .SelectMany(p => p.Reviews)
                .SelectMany(r => r.Findings.Select(f => (r.CreatedAt, f.Severity, f.ResolutionStatus)))
                .ToList();

            int posted = findings.Count;
            int accepted = findings.Count(f => f.ResolutionStatus == ResolutionValues.Accepted);
            int rejected = findings.Count(f => f.ResolutionStatus == ResolutionValues.Rejected);
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
                    accepted = g.Count(f => f.ResolutionStatus == ResolutionValues.Accepted),
                    rejected = g.Count(f => f.ResolutionStatus == ResolutionValues.Rejected),
                };
            }).Where(x => x.posted > 0);

            var weekly = findings
                .GroupBy(f => IsoWeekStart(f.CreatedAt))
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    weekStart = g.Key.ToString("yyyy-MM-dd"),
                    posted = g.Count(),
                    accepted = g.Count(f => f.ResolutionStatus == ResolutionValues.Accepted),
                    rejected = g.Count(f => f.ResolutionStatus == ResolutionValues.Rejected),
                });

            // Nur Zählwerte gebraucht ⇒ DB-seitige Aggregate statt Voll-Load aller Einträge.
            var projectIds = projects.Select(p => p.Id).ToList();
            var memoryQuery = db.MemoryEntries.Where(m => projectIds.Contains(m.ProjectId));
            var memoryEntryCount = await memoryQuery.CountAsync(ctx.RequestAborted);
            var memoryActiveCount = await memoryQuery.CountAsync(m => m.Active, ctx.RequestAborted);
            var memoryTimesApplied = await memoryQuery.SumAsync(m => (int?)m.TimesApplied, ctx.RequestAborted) ?? 0;

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
                    entries = memoryEntryCount,
                    active = memoryActiveCount,
                    timesApplied = memoryTimesApplied,
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
