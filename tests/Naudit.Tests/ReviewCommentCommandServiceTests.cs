// tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class ReviewCommentCommandServiceTests
{
    // Fake-Responder: konfigurierbare Autorisierung, zeichnet gepostete Antworten auf.
    private sealed class FakeResponder(bool authorized) : IReviewCommentResponder
    {
        public List<string> Replies { get; } = new();
        public Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default) => Task.FromResult(authorized);
        public Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
        { Replies.Add(body); return Task.CompletedTask; }
    }

    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    // Seedet ein Projekt+Review mit einem Finding, dessen PlatformCommentId gesetzt ist.
    private static async Task<ReviewFindingEntity> SeedAsync(NauditDbContext db, string platformProjectId, string commentId)
    {
        var project = new ProjectEntity { PlatformProjectId = platformProjectId, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 7, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var finding = new ReviewFindingEntity { Review = review, Severity = "medium", Confidence = "high", File = "src/Foo.cs", Line = 3, Text = "flag", PlatformCommentId = commentId };
        db.ReviewFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding;
    }

    private static ReviewCommentReply Reply(string projectId, string commentId, string? reason = "legacy") =>
        new(projectId, 7, commentId, reason, "bob", AuthorAssociation: "MEMBER", AuthorId: 42);

    [Fact]
    public async Task HandleAsync_marksFp_andReplies_whenAuthorizedAndMatched()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));

        var entry = Assert.Single(db.MemoryEntries);
        Assert.Equal("FalsePositive", entry.Kind);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Equal("legacy", entry.Reason);
        Assert.Equal("bob", entry.CreatedBy);
        Assert.Equal(ReviewCommentCommandService.ConfirmationText, Assert.Single(responder.Replies));
    }

    [Fact]
    public async Task HandleAsync_ignores_whenUnauthorized()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: false);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_ignores_whenNoFindingMatches()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "does-not-exist"));

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_scopesByProject_ignoresSameCommentIdInOtherProject()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/other", "555");   // gleiche Comment-Id, anderes Projekt
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));   // Reply gilt "acme/widgets"

        Assert.Empty(db.MemoryEntries);
        Assert.Empty(responder.Replies);
    }

    [Fact]
    public async Task HandleAsync_idempotent_onSecondCall()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = new ReviewCommentCommandService(db, responder, NullLogger<ReviewCommentCommandService>.Instance);

        await svc.HandleAsync(Reply("acme/widgets", "555"));
        await svc.HandleAsync(Reply("acme/widgets", "555"));

        Assert.Equal(1, await db.MemoryEntries.CountAsync());
    }
}
