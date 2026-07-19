using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class ProjectGuidelinesEntityTests
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

    [Fact]
    public async Task Roundtrip_andUniqueIndexOnProject()
    {
        using var db = NewDb();
        var project = new ProjectEntity { PlatformProjectId = "acme/widgets", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
        {
            ProjectId = project.Id, Markdown = "- rule", SourceHash = "abc",
            DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
        });
        await db.SaveChangesAsync();

        var loaded = await db.ProjectGuidelines.SingleAsync(g => g.ProjectId == project.Id);
        Assert.Equal("- rule", loaded.Markdown);
        Assert.False(loaded.ManuallyEdited);
        Assert.Null(loaded.SourcesChangedAt);

        // Ein Profil pro Projekt: zweite Zeile fürs selbe Projekt verletzt den Unique-Index.
        db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
        {
            ProjectId = project.Id, Markdown = "x", SourceHash = "def",
            DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
