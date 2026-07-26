using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.Logging;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

/// <summary>Schreibpfad des Prompt-Protokolls gegen eine echt migrierte SQLite-DB: beweist, dass
/// die handgepflegte Migration AddChatTranscripts die Tabelle wirklich anlegt und ein Transcript
/// vollständig zurückkommt (EnsureCreated würde die Migration umgehen).</summary>
public class EfChatTranscriptSinkTests
{
    private static NauditDbContext NewMigratedDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"naudit-transcript-{Guid.NewGuid():N}.db");
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>()
            .UseSqlite($"Data Source={path}").Options);
        db.Database.Migrate();
        return db;
    }

    private static ChatTranscript Sample(Guid corr, bool failed = false) => new(
        corr, "owner/repo", 42, "Webhook", "test-model",
        SystemPrompt: "SYS", UserPrompt: "USER-DIFF", ResponseText: "ANSWER",
        InputTokens: 1234, OutputTokens: 56, LatencyMs: 789, ToolCount: 2, Failed: failed);

    [Fact]
    public async Task RecordAsync_roundtrips_afterMigrate()
    {
        await using var db = NewMigratedDb();
        var corr = Guid.NewGuid();
        var sink = new EfChatTranscriptSink(db, NullLogger<EfChatTranscriptSink>.Instance);

        await sink.RecordAsync(Sample(corr));

        var row = await db.ChatTranscripts.SingleAsync();
        Assert.Equal(corr, row.CorrelationId);
        Assert.Equal("owner/repo", row.ProjectId);
        Assert.Equal(42, row.PrNumber);
        Assert.Equal("Webhook", row.Trigger);
        Assert.Equal("test-model", row.Model);
        Assert.Equal("SYS", row.SystemPrompt);
        Assert.Equal("USER-DIFF", row.UserPrompt);
        Assert.Equal("ANSWER", row.ResponseText);
        Assert.Equal(1234, row.InputTokens);
        Assert.Equal(56, row.OutputTokens);
        Assert.Equal(789, row.LatencyMs);
        Assert.Equal(2, row.ToolCount);
        Assert.False(row.Failed);
        Assert.NotEqual(default, row.CreatedAtUtc);
    }

    /// <summary>Ein Review kann mehrere Aufrufe haben (Autor-Session scheitert → globaler Fallback):
    /// beide Zeilen hängen an derselben CorrelationId, so wie das Review-Detail sie joint.</summary>
    [Fact]
    public async Task RecordAsync_mehrereAufrufe_teilenDieKorrelation()
    {
        await using var db = NewMigratedDb();
        var corr = Guid.NewGuid();
        var sink = new EfChatTranscriptSink(db, NullLogger<EfChatTranscriptSink>.Instance);

        await sink.RecordAsync(Sample(corr, failed: true));
        await sink.RecordAsync(Sample(corr));

        var rows = await db.ChatTranscripts.Where(t => t.CorrelationId == corr).OrderBy(t => t.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].Failed);
        Assert.False(rows[1].Failed);
    }

    /// <summary>Die Review-Zeile trägt dieselbe CorrelationId (kein FK) — der Join des
    /// Review-Details muss nach der Migration auf einer echten DB funktionieren.</summary>
    [Fact]
    public async Task ReviewEntity_traegtKorrelation_afterMigrate()
    {
        await using var db = NewMigratedDb();
        var corr = Guid.NewGuid();
        var project = new ProjectEntity
        {
            PlatformProjectId = "owner/repo",
            FirstReviewedAt = DateTime.UtcNow,
            LastReviewedAt = DateTime.UtcNow,
        };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        db.Reviews.Add(new ReviewEntity
        {
            ProjectId = project.Id,
            PrNumber = 42,
            Title = "Test-PR",
            Verdict = "approve",
            Summary = "ok",
            CorrelationId = corr,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var review = await db.Reviews.SingleAsync();
        Assert.Equal(corr, review.CorrelationId);
    }
}
