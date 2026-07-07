using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Lese-API fürs Dashboard/Profil + read-only Settings. Nicht-Admins sehen nur
/// Projekte, deren Owner in den eigenen GitHub-Links liegt.</summary>
public static class DataEndpoints
{
    public static void MapDataEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/dashboard", async (HttpContext ctx, NauditDbContext db) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var projects = await VisibleProjects(db, acct).Include(p => p.Reviews).ToListAsync(ctx.RequestAborted);
            var reviews = projects.SelectMany(p => p.Reviews.Select(r => (Project: p, Review: r))).ToList();

            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var weekStart = today.AddDays(-7);
            var days = Enumerable.Range(0, 30).Select(i => today.AddDays(-29 + i)).ToList();

            return Results.Ok(new
            {
                tokensMonth = reviews.Where(x => x.Review.CreatedAt >= monthStart).Sum(x => Tokens(x.Review)),
                reviewsTotal = reviews.Count,
                reviewsWeek = reviews.Count(x => x.Review.CreatedAt >= weekStart),
                projectsTotal = projects.Count,
                projectsNewMonth = projects.Count(p => p.FirstReviewedAt >= monthStart),
                tokensPerDay = days.Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    tokens = reviews.Where(x => x.Review.CreatedAt.Date == d).Sum(x => Tokens(x.Review)),
                }),
                reviewsPerDay = days.Select(d => new
                {
                    date = d.ToString("yyyy-MM-dd"),
                    count = reviews.Count(x => x.Review.CreatedAt.Date == d),
                }),
                projects = projects
                    .OrderByDescending(p => p.LastReviewedAt)
                    .Select(p => new
                    {
                        id = p.Id,
                        name = p.PlatformProjectId,
                        lastReviewedAt = p.LastReviewedAt,
                        totalTokens = p.Reviews.Sum(Tokens),
                        reviews = p.Reviews.OrderByDescending(r => r.CreatedAt).Take(5)
                            .Select(r => new { id = r.Id, prNumber = r.PrNumber, title = r.Title, verdict = r.Verdict }),
                    }),
                recentReviews = reviews
                    .OrderByDescending(x => x.Review.CreatedAt).Take(10)
                    .Select(x => new
                    {
                        id = x.Review.Id,
                        prNumber = x.Review.PrNumber,
                        title = x.Review.Title,
                        project = x.Project.PlatformProjectId,
                        verdict = x.Review.Verdict,
                        totalTokens = Tokens(x.Review),
                        createdAt = x.Review.CreatedAt,
                    }),
            });
        });

        api.MapGet("/reviews/{id:int}", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var review = await db.Reviews.Include(r => r.Project).Include(r => r.Findings)
                .SingleOrDefaultAsync(r => r.Id == id, ctx.RequestAborted);
            if (review is null) return Results.NotFound();

            if (!acct.IsAdmin)
            {
                var visible = await VisibleProjects(db, acct).AnyAsync(p => p.Id == review.ProjectId, ctx.RequestAborted);
                if (!visible) return Results.Forbid();
            }

            return Results.Ok(new
            {
                id = review.Id,
                prNumber = review.PrNumber,
                title = review.Title,
                project = review.Project.PlatformProjectId,
                verdict = review.Verdict,
                summary = review.Summary,
                model = review.Model,
                inputTokens = review.InputTokens,
                outputTokens = review.OutputTokens,
                createdAt = review.CreatedAt,
                findings = review.Findings.Select(f => new
                {
                    severity = f.Severity,
                    confidence = f.Confidence,
                    file = f.File,
                    line = f.Line,
                    text = f.Text,
                }),
            });
        });

        api.MapGet("/me/usage", async (HttpContext ctx, NauditDbContext db) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var projects = await VisibleProjects(db, acct).Include(p => p.Reviews).ToListAsync(ctx.RequestAborted);
            var reviews = projects.SelectMany(p => p.Reviews.Select(r => (Project: p, Review: r))).ToList();

            var now = DateTime.UtcNow;
            var months = Enumerable.Range(0, 6)
                .Select(i => now.AddMonths(-5 + i))
                .Select(d => new DateTime(d.Year, d.Month, 1, 0, 0, 0, DateTimeKind.Utc))
                .ToList();
            var monthStart = months[^1];

            return Results.Ok(new
            {
                monthly = months.Select(m => new
                {
                    month = m.ToString("yyyy-MM"),
                    tokens = reviews.Where(x => x.Review.CreatedAt >= m && x.Review.CreatedAt < m.AddMonths(1)).Sum(x => Tokens(x.Review)),
                }),
                reviewsTotal = reviews.Count,
                avgTokens = reviews.Count == 0 ? 0 : (long)reviews.Average(x => Tokens(x.Review)),
                perProject = projects
                    .Select(p => new { name = p.PlatformProjectId, tokens = p.Reviews.Where(r => r.CreatedAt >= monthStart).Sum(Tokens) })
                    .Where(x => x.tokens > 0)
                    .OrderByDescending(x => x.tokens),
            });
        });

        // Read-only per Design-Entscheidung: zeigt effektive Config, ändert NICHTS, maskiert alles Geheime.
        api.MapGet("/settings", async (HttpContext ctx, NauditDbContext db, AiOptions ai, GitOptions git,
            ReviewOptions review, UiOptions ui, IOptions<GitHubOptions> gitHub) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            return Results.Ok(new
            {
                ai = new { provider = ai.Provider.ToString(), model = ai.Model },
                git = new
                {
                    platform = git.Platform.ToString(),
                    auth = git.Platform == GitPlatformKind.GitHub ? gitHub.Value.Auth.ToString() : null,
                    postVerdict = git.Platform == GitPlatformKind.GitHub && gitHub.Value.PostVerdict,
                },
                authMethods = new { local = true, gitHub = ui.Auth.GitHub.Enabled, oidc = ui.Auth.Oidc.Enabled },
                systemPrompt = review.SystemPrompt == PromptBuilder.DefaultSystemPrompt ? "built-in default" : "custom (configured)",
            });
        });
    }

    private static long Tokens(ReviewEntity r) => (r.InputTokens ?? 0) + (r.OutputTokens ?? 0);

    /// <summary>Admin: alle Projekte. Sonst: Projekte, deren Owner-Anteil in den eigenen Links liegt.</summary>
    private static IQueryable<ProjectEntity> VisibleProjects(NauditDbContext db, AccountEntity acct)
    {
        if (acct.IsAdmin) return db.Projects;
        var logins = db.GitHubLinks.Where(l => l.AccountId == acct.Id).Select(l => l.Login);
        // Owner = Teil vor '/'; GitLab-Ids matchen als Ganzes (Links sind lowercased gespeichert).
        return db.Projects.Where(p =>
            logins.Any(l => p.PlatformProjectId.ToLower() == l || EF.Functions.Like(p.PlatformProjectId.ToLower(), l + "/%")));
    }
}
