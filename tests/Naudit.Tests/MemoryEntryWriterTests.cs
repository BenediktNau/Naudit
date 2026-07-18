using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class MemoryEntryWriterTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<ReviewFindingEntity> SeedFindingAsync(NauditDbContext db)
    {
        var project = new ProjectEntity { PlatformProjectId = "acme/widgets", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 7, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var finding = new ReviewFindingEntity { Review = review, Severity = "medium", Confidence = "high", File = "src/Foo.cs", Line = 3, Text = "flag", PlatformCommentId = "555" };
        db.ReviewFindings.Add(finding);
        await db.SaveChangesAsync();
        return finding;
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_createsEntryFromFinding()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var entry = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "because legacy", "bob");

        Assert.Equal("FalsePositive", entry.Kind);
        Assert.Equal("src/Foo.cs", entry.File);
        Assert.Equal("flag", entry.Text);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Equal("because legacy", entry.Reason);
        Assert.Equal("bob", entry.CreatedBy);
        Assert.True(entry.Active);
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_isIdempotent_reactivatesAndUpdatesReason()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var first = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, null, "bob");
        first.Active = false;                       // simuliere ein zwischenzeitliches Undo
        await db.SaveChangesAsync();

        var second = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "now with reason", "carol");

        Assert.Equal(first.Id, second.Id);          // kein Duplikat
        Assert.True(second.Active);                 // reaktiviert
        Assert.Equal("now with reason", second.Reason);
        Assert.Equal(1, await db.MemoryEntries.CountAsync());
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_capsOverlongReason_toMaxReasonLength()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);
        var overlong = new string('x', MemoryEntryWriter.MaxReasonLength + 100);

        var entry = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, overlong, "bob");

        Assert.Equal(MemoryEntryWriter.MaxReasonLength, entry.Reason!.Length);
        Assert.Equal(new string('x', MemoryEntryWriter.MaxReasonLength), entry.Reason);
    }
}
