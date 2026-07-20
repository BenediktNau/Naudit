using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class ResolutionColumnsTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task ReviewFinding_persistsResolutionColumns()
    {
        using var db = NewDb();
        var project = new ProjectEntity { PlatformProjectId = "acme/x", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 1, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var f = new ReviewFindingEntity
        {
            Review = review, Severity = "high", Confidence = "high", Text = "x",
            ResolutionStatus = "Accepted", ResolutionSource = "Command", ResolvedBy = "bob", ResolvedAtUtc = DateTime.UtcNow,
        };
        db.ReviewFindings.Add(f);
        await db.SaveChangesAsync();

        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("Accepted", loaded.ResolutionStatus);
        Assert.Equal("Command", loaded.ResolutionSource);
        Assert.Equal("bob", loaded.ResolvedBy);
        Assert.NotNull(loaded.ResolvedAtUtc);
    }

    [Fact]
    public async Task MemoryEntry_hasTimesAppliedDefaultZero_andLastApplied()
    {
        using var db = NewDb();
        var project = new ProjectEntity { PlatformProjectId = "acme/x", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.MemoryEntries.Add(new MemoryEntryEntity
        {
            ProjectId = project.Id, Kind = "Convention", Text = "c", CreatedBy = "bob", CreatedAt = DateTime.UtcNow, Active = true,
        });
        await db.SaveChangesAsync();

        var m = await db.MemoryEntries.SingleAsync();
        Assert.Equal(0, m.TimesApplied);
        Assert.Null(m.LastAppliedAtUtc);
    }
}
