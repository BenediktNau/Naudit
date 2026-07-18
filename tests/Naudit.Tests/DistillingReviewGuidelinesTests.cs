using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Guidelines;
using Xunit;

namespace Naudit.Tests;

public class DistillingReviewGuidelinesTests : IDisposable
{
    // Zählender Fake: liefert eine feste Antwort (oder wirft) und protokolliert jeden Call samt Prompt.
    private sealed class RecordingChatClient(string response, bool throws = false) : IChatClient
    {
        public int Calls { get; private set; }
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastPrompt = string.Join("\n", messages.Select(m => m.Text));
            if (throws) throw new InvalidOperationException("LLM down");
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private readonly string _dir = Directory.CreateTempSubdirectory("naudit-guidelines-test").FullName;
    private readonly SqliteConnection _conn = new("DataSource=:memory:");

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private NauditDbContext NewDb()
    {
        if (_conn.State != System.Data.ConnectionState.Open) _conn.Open();
        var opts = new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options;
        var db = new NauditDbContext(opts);
        db.Database.EnsureCreated();
        return db;
    }

    private async Task<ProjectEntity> SeedProjectAsync(NauditDbContext db, string platformId = "acme/widgets")
    {
        var p = new ProjectEntity { PlatformProjectId = platformId, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        db.Projects.Add(p);
        await db.SaveChangesAsync();
        return p;
    }

    private static DistillingReviewGuidelines Sut(NauditDbContext db, IChatClient chat, ReviewOptions? options = null)
        => new(db, chat, new NullPromptRedactor(), options ?? new ReviewOptions(),
            NullLogger<DistillingReviewGuidelines>.Instance);

    [Fact]
    public async Task FirstSight_distills_stores_andReturnsProfile()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"),
            "Webhook endpoints must enqueue and return 200 immediately.");
        var chat = new RecordingChatClient("- Webhook endpoints must enqueue and return 200 immediately.");

        var result = await Sut(db, chat).GetAsync("acme/widgets", _dir);

        Assert.Equal("- Webhook endpoints must enqueue and return 200 immediately.", result);
        // Lackmustest: die Regel aus der Doku hat den Destillat-Prompt erreicht.
        Assert.Contains("must enqueue and return 200", chat.LastPrompt);
        var row = await db.ProjectGuidelines.SingleAsync();
        Assert.Equal("naudit", row.UpdatedBy);
        Assert.False(row.ManuallyEdited);
        Assert.NotEmpty(row.SourceHash);
    }

    [Fact]
    public async Task UnchangedSources_noSecondLlmCall()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "Rule A.");
        var chat = new RecordingChatClient("- Rule A.");
        var sut = Sut(db, chat);

        var first = await sut.GetAsync("acme/widgets", _dir);
        var second = await sut.GetAsync("acme/widgets", _dir);

        Assert.Equal(first, second);
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task ChangedSources_redistills()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        var f = Path.Combine(_dir, "CLAUDE.md");
        File.WriteAllText(f, "Rule A.");
        var chat = new RecordingChatClient("- Rule.");
        var sut = Sut(db, chat);

        await sut.GetAsync("acme/widgets", _dir);
        File.WriteAllText(f, "Rule B.");
        await sut.GetAsync("acme/widgets", _dir);

        Assert.Equal(2, chat.Calls);
    }

    [Fact]
    public async Task ManuallyEdited_blocksAutoRefresh_andSetsStaleSignal()
    {
        using var db = NewDb();
        var project = await SeedProjectAsync(db);
        db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
        {
            ProjectId = project.Id, Markdown = "- kuratierte Regel", SourceHash = "old",
            DistilledAt = DateTime.UtcNow, ManuallyEdited = true, UpdatedBy = "bob",
        });
        await db.SaveChangesAsync();
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "New docs.");
        var chat = new RecordingChatClient("- should not be used");

        var result = await Sut(db, chat).GetAsync("acme/widgets", _dir);

        Assert.Equal("- kuratierte Regel", result);      // menschliche Kuration gewinnt
        Assert.Equal(0, chat.Calls);                     // kein LLM-Call
        var row = await db.ProjectGuidelines.SingleAsync();
        Assert.NotNull(row.SourcesChangedAt);            // Stale-Signal für die WebUI
    }

    [Fact]
    public async Task NoWorkspace_returnsStoredProfile_withoutLlmCall()
    {
        using var db = NewDb();
        var project = await SeedProjectAsync(db);
        db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
        {
            ProjectId = project.Id, Markdown = "- stored", SourceHash = "h",
            DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
        });
        await db.SaveChangesAsync();
        var chat = new RecordingChatClient("x");

        Assert.Equal("- stored", await Sut(db, chat).GetAsync("acme/widgets", null));
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task NoDocs_returnsNull()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        var chat = new RecordingChatClient("x");

        Assert.Null(await Sut(db, chat).GetAsync("acme/widgets", _dir));
        Assert.Equal(0, chat.Calls);
    }

    [Fact]
    public async Task UnknownProject_distills_andReturns_butDoesNotStore()
    {
        // Allererstes Review: die Projekt-Zeile legt erst der Audit-Sink NACH dem Review an
        // (inkl. Ownership) — der Distiller liefert das Profil trotzdem, speichert aber nicht.
        using var db = NewDb();
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "Rule A.");
        var chat = new RecordingChatClient("- Rule A.");

        var result = await Sut(db, chat).GetAsync("acme/widgets", _dir);

        Assert.Equal("- Rule A.", result);
        Assert.Equal(0, await db.ProjectGuidelines.CountAsync());
    }

    [Fact]
    public async Task LlmFailure_failsOpen_toStoredProfile()
    {
        using var db = NewDb();
        var project = await SeedProjectAsync(db);
        db.ProjectGuidelines.Add(new ProjectGuidelinesEntity
        {
            ProjectId = project.Id, Markdown = "- old profile", SourceHash = "stale",
            DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
        });
        await db.SaveChangesAsync();
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "Changed docs.");
        var chat = new RecordingChatClient("ignored", throws: true);

        Assert.Equal("- old profile", await Sut(db, chat).GetAsync("acme/widgets", _dir));
    }

    [Fact]
    public async Task EmptyDistillate_storesHash_returnsNull_andSkipsNextCall()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), "No rules here, just prose.");
        var chat = new RecordingChatClient("");
        var sut = Sut(db, chat);

        Assert.Null(await sut.GetAsync("acme/widgets", _dir));
        Assert.Null(await sut.GetAsync("acme/widgets", _dir));   // Hash gespeichert ⇒ kein zweiter Call
        Assert.Equal(1, chat.Calls);
    }

    [Fact]
    public async Task Caps_skipOversizedSource_andTruncateProfile()
    {
        using var db = NewDb();
        await SeedProjectAsync(db);
        var options = new ReviewOptions();
        options.Guidelines.MaxSourceChars = 20;
        options.Guidelines.MaxProfileChars = 10;
        File.WriteAllText(Path.Combine(_dir, "CLAUDE.md"), new string('x', 100));  // > MaxSourceChars ⇒ ganz übersprungen
        File.WriteAllText(Path.Combine(_dir, "README.md"), "short rule");
        var chat = new RecordingChatClient("0123456789ABCDEF");

        var result = await Sut(db, chat, options).GetAsync("acme/widgets", _dir);

        Assert.DoesNotContain("xxxx", chat.LastPrompt);          // übergroße Quelle nicht im Prompt
        Assert.Contains("short rule", chat.LastPrompt);
        Assert.Equal("0123456789", result);                      // Profil auf MaxProfileChars gedeckelt
    }
}
