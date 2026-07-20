// tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class ReviewCommentCommandServiceTests
{
    // Simuliert das echte Doppel-POST-Race in MemoryEntryWriter.MarkFalsePositiveAsync deterministisch:
    // kurz VOR dem ersten SaveChangesAsync-Commit des getrackten MemoryEntry-Inserts schreibt ein
    // ZWEITER Kontext (dieselbe SQLite-Connection, am ChangeTracker des ersten Kontexts vorbei) bereits
    // einen Eintrag mit derselben SourceFindingId — der Unique-Index lässt den getrackten Insert danach
    // mit einer echten DbUpdateException scheitern, genau wie beim realen Race zweier Prozesse.
    private sealed class ConcurrentInsertInterceptor(DbContextOptions<NauditDbContext> opts, int findingId, int projectId)
        : SaveChangesInterceptor
    {
        private bool _fired;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (!_fired && eventData.Context is not null
                && eventData.Context.ChangeTracker.Entries<MemoryEntryEntity>().Any(e => e.State == EntityState.Added))
            {
                _fired = true;
                using var other = new NauditDbContext(opts);
                other.MemoryEntries.Add(new MemoryEntryEntity
                {
                    ProjectId = projectId,
                    Kind = "FalsePositive",
                    File = "src/Foo.cs",
                    Text = "flag",
                    SourceFindingId = findingId,
                    CreatedBy = "other",
                    CreatedAt = DateTime.UtcNow,
                    Active = true,
                });
                await other.SaveChangesAsync(ct);
            }
            return await base.SavingChangesAsync(eventData, result, ct);
        }
    }

    // Fake-Responder: konfigurierbare Autorisierung, zeichnet gepostete Antworten auf.
    // throwOnReply simuliert einen fehlschlagenden Bestätigungs-Post (best-effort, siehe T7-Test unten).
    private sealed class FakeResponder(bool authorized, bool throwOnReply = false) : IReviewCommentResponder
    {
        public List<string> Replies { get; } = new();
        public Task<bool> IsAuthorizedAsync(ReviewCommentReply reply, CancellationToken ct = default) => Task.FromResult(authorized);
        public Task PostReplyAsync(ReviewCommentReply reply, string body, CancellationToken ct = default)
        {
            if (throwOnReply)
                throw new InvalidOperationException("Antwort-Post fehlgeschlagen (simuliert).");
            Replies.Add(body);
            return Task.CompletedTask;
        }
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

    // Seedet ZWEI Findings im selben Projekt/Review mit DERSELBEN PlatformCommentId (Mehrdeutigkeits-Kante
    // aus PR 2a — z.B. zwei Findings auf Datei+Zeile teilen sich einen GitHub-Comment). Reihenfolge der
    // Erzeugung ist absichtlich so gewählt, dass die erwartete "erste" (kleinste Id) nicht zufällig die
    // erstgeseedete ist.
    private static async Task<(ReviewFindingEntity First, ReviewFindingEntity Second)> SeedAmbiguousAsync(
        NauditDbContext db, string platformProjectId, string commentId)
    {
        var project = new ProjectEntity { PlatformProjectId = platformProjectId, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 7, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var first = new ReviewFindingEntity { Review = review, Severity = "medium", Confidence = "high", File = "src/Foo.cs", Line = 3, Text = "flag A", PlatformCommentId = commentId };
        var second = new ReviewFindingEntity { Review = review, Severity = "low", Confidence = "medium", File = "src/Foo.cs", Line = 3, Text = "flag B", PlatformCommentId = commentId };
        db.ReviewFindings.AddRange(first, second);
        await db.SaveChangesAsync();
        return (first, second);
    }

    private static ReviewCommentReply Reply(string projectId, string commentId, string? reason = "legacy") =>
        new(projectId, 7, commentId, reason, "bob", AuthorAssociation: "MEMBER", AuthorId: 42, Command: ReviewCommandKind.FalsePositive);

    // "@naudit ok"-Kommando — gleiche Adressierung wie Reply(), aber Command = Accept.
    private static ReviewCommentReply AcceptReply(string projectId, string commentId) =>
        new(projectId, 7, commentId, null, "bob", AuthorAssociation: "MEMBER", AuthorId: 42, Command: ReviewCommandKind.Accept);

    // Baut den Service mit einer ReviewOptions-Instanz, deren Resolution-Schalter steuerbar ist
    // (Ctor-Parameter aus Review-Analytics PR 3, Task 5).
    private static ReviewCommentCommandService Service(NauditDbContext db, FakeResponder responder, bool resolutionEnabled = true) =>
        new(db, responder, NullLogger<ReviewCommentCommandService>.Instance,
            new ReviewOptions { Resolution = new ReviewResolutionOptions { Enabled = resolutionEnabled } });

    [Fact]
    public async Task HandleAsync_marksFp_andReplies_whenAuthorizedAndMatched()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);

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
        var svc = Service(db, responder);

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
        var svc = Service(db, responder);

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
        var svc = Service(db, responder);

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
        var svc = Service(db, responder);

        await svc.HandleAsync(Reply("acme/widgets", "555"));
        await svc.HandleAsync(Reply("acme/widgets", "555"));

        Assert.Equal(1, await db.MemoryEntries.CountAsync());
    }

    [Fact]
    public async Task HandleAsync_secondCall_doesNotPostSecondConfirmation()
    {
        // Redelivery-Schutz (Fix 3): ein bereits als FP markiertes Finding löst bei einem erneuten
        // Kommando keine zweite Bestätigungs-Antwort im Thread aus.
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);

        await svc.HandleAsync(Reply("acme/widgets", "555"));
        Assert.Single(responder.Replies);   // die erste Antwort bestätigt

        await svc.HandleAsync(Reply("acme/widgets", "555"));
        Assert.Single(responder.Replies);   // die zweite bleibt aus — Replies-Zähler unverändert
    }

    [Fact]
    public async Task HandleAsync_ambiguousCommentId_anchorsToFirstFindingById_andCreatesOnlyOneEntry()
    {
        using var db = NewDb();
        var (first, second) = await SeedAmbiguousAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);

        await svc.HandleAsync(Reply("acme/widgets", "555"));

        var entry = Assert.Single(db.MemoryEntries);
        Assert.Equal(first.Id, entry.SourceFindingId);
        Assert.NotEqual(second.Id, entry.SourceFindingId);
        Assert.Equal(ReviewCommentCommandService.ConfirmationText, Assert.Single(responder.Replies));
    }

    [Fact]
    public async Task HandleAsync_bestEffortReplyThrows_entryStillRecorded_andHandleAsyncDoesNotThrow()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true, throwOnReply: true);
        var svc = Service(db, responder);

        // Wirft NICHT, obwohl der Bestätigungs-Post fehlschlägt — die Zuordnung ist bereits gespeichert.
        await svc.HandleAsync(Reply("acme/widgets", "555"));

        var entry = Assert.Single(db.MemoryEntries);
        Assert.Equal(finding.Id, entry.SourceFindingId);
        Assert.Empty(responder.Replies);   // der (fehlgeschlagene) Post wurde nicht aufgezeichnet
    }

    [Fact]
    public async Task Fp_alsoWritesRejectedResolution()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);   // Helper baut den Service mit ReviewOptions (Resolution.Enabled=true)

        await svc.HandleAsync(Reply("acme/widgets", "555"));   // fp

        var f = await db.ReviewFindings.SingleAsync(x => x.Id == finding.Id);
        Assert.Equal("Rejected", f.ResolutionStatus);
        Assert.Equal("Command", f.ResolutionSource);
        Assert.Single(db.MemoryEntries);   // fp weiterhin im Gedächtnis
    }

    [Fact]
    public async Task Fp_concurrentMemoryInsertRace_resolutionStillPersists()
    {
        // Regression: MemoryEntryWriter.MarkFalsePositiveAsync ruft im DbUpdateException-Catch-Zweig
        // (Doppel-POST-Race auf dem SourceFindingId-Unique-Index) db.ChangeTracker.Clear() auf — das
        // detached "finding". Würde die Resolution-Schreibung NACH dem Gedächtnis-Mark laufen, würde
        // sie ein bereits detachtes Finding mutieren: SaveChangesAsync liefe durch (kein Fehler), aber
        // OHNE etwas zu persistieren. Der Fix schreibt die Resolution ZUERST — bevor das Race auftreten
        // kann. Mit der alten Reihenfolge schlägt dieser Test fehl (ResolutionStatus bliebe null).
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options;
        using (var seedDb = new NauditDbContext(opts))
        {
            seedDb.Database.EnsureCreated();
            var seeded = await SeedAsync(seedDb, "acme/widgets", "555");

            var raceOpts = new DbContextOptionsBuilder<NauditDbContext>()
                .UseSqlite(conn)
                .AddInterceptors(new ConcurrentInsertInterceptor(opts, seeded.Id, seeded.Review.ProjectId))
                .Options;
            using var db = new NauditDbContext(raceOpts);
            var responder = new FakeResponder(authorized: true);
            var svc = Service(db, responder);

            await svc.HandleAsync(Reply("acme/widgets", "555"));   // fp — löst intern das Race aus
        }

        // Frischer, unabhängiger Kontext auf derselben Connection — beweist, was tatsächlich in der DB steht.
        using var verifyDb = new NauditDbContext(opts);
        var f = await verifyDb.ReviewFindings.AsNoTracking().SingleAsync();
        Assert.Equal("Rejected", f.ResolutionStatus);
        Assert.Equal("Command", f.ResolutionSource);
        // Das Gedächtnis-Race selbst wurde idempotent aufgelöst — genau EIN aktiver Eintrag.
        var entry = await verifyDb.MemoryEntries.AsNoTracking().SingleAsync();
        Assert.True(entry.Active);
    }

    [Fact]
    public async Task Ok_writesAcceptedResolution_confirms_noMemoryEntry()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);

        await svc.HandleAsync(AcceptReply("acme/widgets", "555"));   // Command = Accept

        var f = await db.ReviewFindings.SingleAsync(x => x.Id == finding.Id);
        Assert.Equal("Accepted", f.ResolutionStatus);
        Assert.Equal("Command", f.ResolutionSource);
        Assert.Empty(db.MemoryEntries);
        Assert.Equal(ReviewCommentCommandService.AcceptConfirmationText, Assert.Single(responder.Replies));
    }

    [Fact]
    public async Task Ok_secondDelivery_doesNotConfirmAgain()
    {
        using var db = NewDb();
        await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);
        await svc.HandleAsync(AcceptReply("acme/widgets", "555"));
        await svc.HandleAsync(AcceptReply("acme/widgets", "555"));
        Assert.Single(responder.Replies);
    }

    [Fact]
    public async Task ResolutionDisabled_ok_isNoOp_fpStillMarks()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder, resolutionEnabled: false);

        await svc.HandleAsync(AcceptReply("acme/widgets", "555"));
        Assert.Null((await db.ReviewFindings.SingleAsync(x => x.Id == finding.Id)).ResolutionStatus);
        Assert.Empty(responder.Replies);

        await svc.HandleAsync(Reply("acme/widgets", "555"));   // fp
        Assert.Single(db.MemoryEntries);                        // Memory unabhängig vom Resolution-Schalter
        Assert.Null((await db.ReviewFindings.SingleAsync(x => x.Id == finding.Id)).ResolutionStatus);
    }
}
