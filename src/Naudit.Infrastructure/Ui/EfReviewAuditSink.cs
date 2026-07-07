using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Persistiert Review-Audits: Projekt-Upsert (Auto-Registrierung beim 1. Review,
/// inkl. Owner-Zuordnung zum aktiven Account) + Review + Findings.</summary>
public sealed class EfReviewAuditSink(NauditDbContext db, ILogger<EfReviewAuditSink> logger) : IReviewAuditSink
{
    public async Task RecordAsync(ReviewAudit audit, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var project = await db.Projects.SingleOrDefaultAsync(p => p.PlatformProjectId == audit.ProjectId, ct);
        if (project is null)
        {
            var owner = EfAccessGate.OwnerOf(audit.ProjectId);
            var accountId = await db.GitHubLinks
                .Where(l => l.Login == owner && l.Account.Status == AccountStatus.Active)
                .Select(l => (int?)l.AccountId)
                .FirstOrDefaultAsync(ct);
            project = new ProjectEntity { PlatformProjectId = audit.ProjectId, AccountId = accountId, FirstReviewedAt = now };
            db.Projects.Add(project);
        }
        project.LastReviewedAt = now;

        var review = new ReviewEntity
        {
            Project = project,
            PrNumber = audit.MergeRequestIid,
            Title = audit.Title,
            Verdict = audit.Verdict == ReviewVerdict.RequestChanges ? "request_changes" : "approve",
            Summary = audit.Summary,
            InputTokens = audit.InputTokens,
            OutputTokens = audit.OutputTokens,
            Model = audit.Model,
            CreatedAt = now,
        };
        foreach (var f in audit.Findings)
            review.Findings.Add(new ReviewFindingEntity
            {
                Severity = f.Severity.ToString(),
                Confidence = f.Confidence.ToString(),
                File = f.File,
                Line = f.Line,
                Text = f.Text,
            });
        db.Reviews.Add(review);

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Review-Audit gespeichert: {Project}#{Pr} → {Verdict}.",
            audit.ProjectId, audit.MergeRequestIid, review.Verdict);
    }
}
