using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class EfReviewAuditSinkTests
{
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static ReviewAudit Audit(string project = "owner/repo", int pr = 7) => new(
        project, pr, "Titel", ReviewVerdict.RequestChanges, "Summary",
        [new AuditFinding(FindingSeverity.High, ReviewConfidence.High, "a.cs", 3, "Fund")],
        1234, 56, "claude-sonnet-4-6");

    [Fact]
    public async Task Record_upsertsProject_insertsReviewWithFindings()
    {
        await using var db = NewDb();
        var sink = new EfReviewAuditSink(db, NullLogger<EfReviewAuditSink>.Instance);

        await sink.RecordAsync(Audit());
        await sink.RecordAsync(Audit(pr: 8)); // zweiter Review, gleiches Projekt

        var project = await db.Projects.Include(p => p.Reviews).ThenInclude(r => r.Findings).SingleAsync();
        Assert.Equal("owner/repo", project.PlatformProjectId);
        Assert.Equal(2, project.Reviews.Count);
        Assert.Equal("request_changes", project.Reviews[0].Verdict);
        Assert.Equal(1234, project.Reviews[0].InputTokens);
        Assert.Single(project.Reviews[0].Findings);
    }

    [Fact]
    public async Task Record_linksProjectToOwningActiveAccount()
    {
        await using var db = NewDb();
        var acct = new AccountEntity { Username = "o", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        acct.GitHubLinks.Add(new GitHubLinkEntity { Login = "owner" });
        db.Accounts.Add(acct);
        await db.SaveChangesAsync();

        var sink = new EfReviewAuditSink(db, NullLogger<EfReviewAuditSink>.Instance);
        await sink.RecordAsync(Audit());

        Assert.Equal(acct.Id, (await db.Projects.SingleAsync()).AccountId);
    }

    [Fact]
    public async Task Record_persistsAiSessionAccountId()
    {
        using var db = new TestDb();
        var acct = new AccountEntity { Username = "alice", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(acct);
        await db.Context.SaveChangesAsync();
        var sink = new EfReviewAuditSink(db.Context, NullLogger<EfReviewAuditSink>.Instance);

        await sink.RecordAsync(new ReviewAudit("o/r", 1, "T", ReviewVerdict.Approve, "S", [], 1, 1, "m",
            AiSessionAccountId: acct.Id));

        Assert.Equal(acct.Id, db.Context.Reviews.Single().AiSessionAccountId);
    }

    [Fact]
    public async Task RecordAsync_persistsPlatformCommentAndNoteIds()
    {
        using var test = new TestDb();
        var sink = new EfReviewAuditSink(test.Context, NullLogger<EfReviewAuditSink>.Instance);
        var audit = new ReviewAudit("owner/repo", 1, "T", ReviewVerdict.Approve, "S",
            [new AuditFinding(FindingSeverity.High, ReviewConfidence.High, "a.cs", 1, "f", "gh-1", "gl-9")],
            null, null, null);

        await sink.RecordAsync(audit);

        var f = await test.Context.ReviewFindings.SingleAsync();
        Assert.Equal("gh-1", f.PlatformCommentId);
        Assert.Equal("gl-9", f.PlatformNoteId);
    }
}
