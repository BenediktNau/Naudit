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

        var result = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "because legacy", "bob");
        var entry = result.Entry;

        Assert.Equal("FalsePositive", entry.Kind);
        Assert.Equal("src/Foo.cs", entry.File);
        Assert.Equal("flag", entry.Text);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Equal("because legacy", entry.Reason);
        Assert.Equal("bob", entry.CreatedBy);
        Assert.True(entry.Active);
        Assert.True(result.NewlyMarked);            // erstmalig angelegt ⇒ echter Zustandswechsel
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_isIdempotent_reactivatesAndUpdatesReason()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var first = (await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, null, "bob")).Entry;
        first.Active = false;                       // simuliere ein zwischenzeitliches Undo
        await db.SaveChangesAsync();

        var secondResult = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "now with reason", "carol");
        var second = secondResult.Entry;

        Assert.Equal(first.Id, second.Id);          // kein Duplikat
        Assert.True(second.Active);                 // reaktiviert
        Assert.Equal("now with reason", second.Reason);
        Assert.Equal(1, await db.MemoryEntries.CountAsync());
        Assert.True(secondResult.NewlyMarked);       // Reaktivierung aus inaktiv ⇒ echter Zustandswechsel
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_secondCallOnAlreadyActive_reportsNotNewlyMarked()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);

        var first = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "because legacy", "bob");
        var second = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, "because legacy again", "bob");

        Assert.True(first.NewlyMarked);
        Assert.False(second.NewlyMarked);            // war bereits aktiv ⇒ kein Zustandswechsel
        Assert.Equal(first.Entry.Id, second.Entry.Id);
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_capsOverlongReason_toMaxReasonLength()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);
        var overlong = new string('x', MemoryEntryWriter.MaxReasonLength + 100);

        var result = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, overlong, "bob");

        Assert.Equal(MemoryEntryWriter.MaxReasonLength, result.Entry.Reason!.Length);
        Assert.Equal(new string('x', MemoryEntryWriter.MaxReasonLength), result.Entry.Reason);
    }

    [Fact]
    public async Task MarkFalsePositiveAsync_capsOverlongReason_withoutSplittingSurrogatePair()
    {
        using var db = NewDb();
        var finding = await SeedFindingAsync(db);
        // MaxReasonLength - 1 gewöhnliche Zeichen + ein Emoji (Surrogat-Paar, 2 UTF-16-Chars) = MaxReasonLength + 1.
        var overlong = new string('x', MemoryEntryWriter.MaxReasonLength - 1) + "\U0001F600";

        var result = await MemoryEntryWriter.MarkFalsePositiveAsync(db, finding, overlong, "bob");

        Assert.Equal(MemoryEntryWriter.MaxReasonLength - 1, result.Entry.Reason!.Length);   // das Emoji wurde GANZ verworfen
        Assert.False(char.IsHighSurrogate(result.Entry.Reason[^1]));                        // kein lone surrogate übrig
    }
}
