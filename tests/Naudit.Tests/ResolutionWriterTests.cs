using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Analytics;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class ResolutionWriterTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    private static async Task<ReviewFindingEntity> SeedFindingAsync(NauditDbContext db)
    {
        var project = new ProjectEntity { PlatformProjectId = "acme/x", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 1, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var f = new ReviewFindingEntity { Review = review, Severity = "high", Confidence = "high", Text = "x" };
        db.ReviewFindings.Add(f);
        await db.SaveChangesAsync();
        return f;
    }

    [Fact]
    public async Task Apply_setsStatusSourceByAt_andReturnsTrue()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Command", "bob");
        Assert.True(changed);
        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("Accepted", loaded.ResolutionStatus);
        Assert.Equal("Command", loaded.ResolutionSource);
        Assert.Equal("bob", loaded.ResolvedBy);
        Assert.NotNull(loaded.ResolvedAtUtc);
    }

    [Fact]
    public async Task Apply_explicitOverwritesLlm()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Rejected", "Llm", "naudit");
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Accepted", "WebUi", "bob");
        Assert.True(changed);
        Assert.Equal("Accepted", (await db.ReviewFindings.SingleAsync()).ResolutionStatus);
        Assert.Equal("WebUi", (await db.ReviewFindings.SingleAsync()).ResolutionSource);
    }

    [Fact]
    public async Task Apply_llmDoesNotOverwriteExplicit()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Command", "bob");
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Rejected", "Llm", "naudit");
        Assert.False(changed);
        Assert.Equal("Accepted", (await db.ReviewFindings.SingleAsync()).ResolutionStatus);
        Assert.Equal("Command", (await db.ReviewFindings.SingleAsync()).ResolutionSource);
    }

    [Fact]
    public async Task Apply_llmOverwritesLlm()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Llm", "naudit");
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Rejected", "Llm", "naudit");
        Assert.True(changed);
        Assert.Equal("Rejected", (await db.ReviewFindings.SingleAsync()).ResolutionStatus);
    }

    [Fact]
    public async Task Apply_undoClearsOnlyItsOwnSource()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Rejected", "Command", "bob");
        // WebUi-Undo darf eine Command-Entscheidung NICHT löschen.
        var changed = await ResolutionWriter.ApplyAsync(db, f, null, "WebUi", "carol");
        Assert.False(changed);
        Assert.Equal("Rejected", (await db.ReviewFindings.SingleAsync()).ResolutionStatus);
        // Command-Undo derselben Quelle löscht.
        var changed2 = await ResolutionWriter.ApplyAsync(db, f, null, "Command", "bob");
        Assert.True(changed2);
        Assert.Null((await db.ReviewFindings.SingleAsync()).ResolutionStatus);
        Assert.Null((await db.ReviewFindings.SingleAsync()).ResolutionSource);
    }

    [Fact]
    public async Task Apply_noChange_whenSameStatusAndSource_returnsFalse()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Command", "bob");
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Command", "bob");
        Assert.False(changed);
    }

    [Fact]
    public async Task Apply_explicitOverwritesExplicit()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Rejected", "Command", "bob");
        // Eine explizite Quelle überschreibt eine andere explizite (kein Guard blockt das).
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Accepted", "WebUi", "carol");
        Assert.True(changed);
        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("Accepted", loaded.ResolutionStatus);
        Assert.Equal("WebUi", loaded.ResolutionSource);
        Assert.Equal("carol", loaded.ResolvedBy);
    }

    [Fact]
    public async Task Apply_sameStatus_explicitOverLlm_updatesSourceAndBy()
    {
        using var db = NewDb();
        var f = await SeedFindingAsync(db);
        await ResolutionWriter.ApplyAsync(db, f, "Accepted", "Llm", "naudit");
        // Gleicher Status, aber explizite Quelle über LLM: kein No-Op — Quelle/Autor werden aktualisiert.
        var changed = await ResolutionWriter.ApplyAsync(db, f, "Accepted", "WebUi", "carol");
        Assert.True(changed);
        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("Accepted", loaded.ResolutionStatus);
        Assert.Equal("WebUi", loaded.ResolutionSource);
        Assert.Equal("carol", loaded.ResolvedBy);
    }
}
