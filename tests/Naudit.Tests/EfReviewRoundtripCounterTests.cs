using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

public class EfReviewRoundtripCounterTests
{
    private static NauditDbContext NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-test-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static ReviewAudit Audit(string project, int pr) => new(
        project, pr, "Titel", ReviewVerdict.Approve, "Summary", [], null, null, null);

    [Fact]
    public async Task Count_countsOnlyMatchingProjectAndPr()
    {
        await using var db = NewDb();
        // Über den Audit-Sink seeden — der Zähler zählt genau das, was der Sink schreibt.
        var sink = new EfReviewAuditSink(db, NullLogger<EfReviewAuditSink>.Instance);
        await sink.RecordAsync(Audit("owner/repo", 7));
        await sink.RecordAsync(Audit("owner/repo", 7));
        await sink.RecordAsync(Audit("owner/repo", 8));   // anderer PR
        await sink.RecordAsync(Audit("other/repo", 7));   // anderes Projekt

        var counter = new EfReviewRoundtripCounter(db);

        Assert.Equal(2, await counter.CountAsync("owner/repo", 7));
        Assert.Equal(1, await counter.CountAsync("owner/repo", 8));
        Assert.Equal(0, await counter.CountAsync("owner/repo", 99)); // nie reviewt
    }
}
