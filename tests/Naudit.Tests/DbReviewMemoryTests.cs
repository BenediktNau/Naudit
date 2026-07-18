using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Memory;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DbReviewMemoryTests
{
    /// <summary>Temp-File-SQLite + Database.Migrate() — beweist, dass die handgepflegte
    /// Migration auf SQLite wirklich läuft (EnsureCreated würde sie umgehen).</summary>
    private static NauditDbContext NewMigratedDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-memory-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static ProjectEntity SeedProject(NauditDbContext db, string platformId = "owner/repo")
    {
        var p = new ProjectEntity { PlatformProjectId = platformId, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        db.Projects.Add(p);
        db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task MemoryEntry_roundtrips_afterMigrate()
    {
        await using var db = NewMigratedDb();
        var project = SeedProject(db);

        db.MemoryEntries.Add(new MemoryEntryEntity
        {
            ProjectId = project.Id, Kind = "Convention", Text = "Wir nutzen X",
            CreatedBy = "root", CreatedAt = DateTime.UtcNow, Active = true,
        });
        await db.SaveChangesAsync();

        var loaded = await db.MemoryEntries.SingleAsync();
        Assert.Equal("Convention", loaded.Kind);
        Assert.True(loaded.Active);
        Assert.Null(loaded.SourceFindingId);
    }

    private static MemoryEntryEntity Entry(int projectId, string kind, string? file, string text,
        bool active = true, DateTime? createdAt = null) => new()
    {
        ProjectId = projectId, Kind = kind, File = file, Text = text,
        CreatedBy = "root", CreatedAt = createdAt ?? DateTime.UtcNow, Active = active,
    };

    private static List<CodeChange> Changes(params string[] files)
        => files.Select(f => new CodeChange(f, "@@ -0,0 +1,1 @@\n+x")).ToList();

    [Fact]
    public async Task Select_matchesFileScopedFps_includesFileless_excludesOthers()
    {
        using var test = new TestDb();
        var db = test.Context;
        var p = SeedProject(db);
        db.MemoryEntries.AddRange(
            Entry(p.Id, "FalsePositive", "src/A.cs", "FP-A"),
            Entry(p.Id, "FalsePositive", "src/B.cs", "FP-B"),        // nicht im Diff ⇒ raus
            Entry(p.Id, "FalsePositive", null, "FP-ohne-Datei"),
            Entry(p.Id, "Convention", "src/Zzz.cs", "Konvention"),   // File nur Anzeige ⇒ immer dabei
            Entry(p.Id, "FalsePositive", "src/A.cs", "inaktiv", active: false));
        await db.SaveChangesAsync();

        var memory = new DbReviewMemory(db, new ReviewOptions(), NullLogger<DbReviewMemory>.Instance);
        var result = await memory.SelectAsync("owner/repo", Changes("src/A.cs"));

        Assert.Equal(3, result.Count);
        Assert.Contains(result, m => m.Text == "FP-A");
        Assert.Contains(result, m => m.Text == "FP-ohne-Datei");
        Assert.Contains(result, m => m.Kind == MemoryKind.Convention && m.Text == "Konvention");
        Assert.DoesNotContain(result, m => m.Text == "FP-B");
        Assert.DoesNotContain(result, m => m.Text == "inaktiv");
    }

    [Fact]
    public async Task Select_capsAtMaxEntries_conventionsFirst_newestFirst()
    {
        using var test = new TestDb();
        var db = test.Context;
        var p = SeedProject(db);
        var t0 = DateTime.UtcNow;
        db.MemoryEntries.AddRange(
            Entry(p.Id, "FalsePositive", null, "FP-alt", createdAt: t0.AddDays(-2)),
            Entry(p.Id, "FalsePositive", null, "FP-neu", createdAt: t0),
            Entry(p.Id, "Convention", null, "Konv-alt", createdAt: t0.AddDays(-3)),
            Entry(p.Id, "Convention", null, "Konv-neu", createdAt: t0.AddDays(-1)));
        await db.SaveChangesAsync();

        var options = new ReviewOptions { Memory = { MaxEntries = 3 } };
        var memory = new DbReviewMemory(db, options, NullLogger<DbReviewMemory>.Instance);
        var result = await memory.SelectAsync("owner/repo", Changes("x.cs"));

        // Konventionen zuerst (beide), dann der NEUSTE FP — der alte fällt dem Deckel zum Opfer.
        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "Konv-neu", "Konv-alt", "FP-neu" }, result.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task Select_unknownProject_returnsEmpty()
    {
        using var test = new TestDb();
        var memory = new DbReviewMemory(test.Context, new ReviewOptions(), NullLogger<DbReviewMemory>.Instance);
        Assert.Empty(await memory.SelectAsync("nobody/nothing", Changes("x.cs")));
    }

    [Fact]
    public async Task Select_dbFailure_failsOpenToEmpty()
    {
        var test = new TestDb();
        var db = test.Context;
        test.Dispose();                                            // Verbindung tot ⇒ jede Query wirft
        var memory = new DbReviewMemory(db, new ReviewOptions(), NullLogger<DbReviewMemory>.Instance);
        Assert.Empty(await memory.SelectAsync("owner/repo", Changes("x.cs")));
    }

    [Fact]
    public async Task Select_incrementsTimesApplied_onSelectedEntries()
    {
        using var test = new TestDb();
        var db = test.Context;
        var p = SeedProject(db);
        db.MemoryEntries.Add(Entry(p.Id, "Convention", null, "Konvention"));
        await db.SaveChangesAsync();

        var memory = new DbReviewMemory(db, new ReviewOptions(), NullLogger<DbReviewMemory>.Instance);
        await memory.SelectAsync("owner/repo", Changes("x.cs"));

        var m = await db.MemoryEntries.SingleAsync();
        Assert.Equal(1, m.TimesApplied);
        Assert.NotNull(m.LastAppliedAtUtc);

        await memory.SelectAsync("owner/repo", Changes("x.cs"));
        Assert.Equal(2, (await db.MemoryEntries.SingleAsync()).TimesApplied);
    }

    [Fact]
    public async Task ReviewFinding_persistsPlatformCommentAndNoteIds_afterMigrate()
    {
        await using var db = NewMigratedDb();
        var project = SeedProject(db);
        var review = new ReviewEntity
        {
            ProjectId = project.Id, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
            CreatedAt = DateTime.UtcNow,
            Findings = { new ReviewFindingEntity
            {
                Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "f",
                PlatformCommentId = "gh-12345", PlatformNoteId = "gl-note-678",
            } },
        };
        db.Reviews.Add(review);
        await db.SaveChangesAsync();

        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("gh-12345", loaded.PlatformCommentId);
        Assert.Equal("gl-note-678", loaded.PlatformNoteId);
    }
}
