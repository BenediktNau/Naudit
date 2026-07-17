using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
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
}
