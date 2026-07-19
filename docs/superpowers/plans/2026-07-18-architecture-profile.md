# Architecture Profile (Distilled Guidelines) & Review Altitude Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit distills a compact, curatable architecture profile from the reviewed repository's own docs and feeds it — plus explicit architecture-altitude and security instructions — into every review prompt, so architecture-level findings (like PR #63's missed webhook-queue violation) become reachable.

**Architecture:** New Core seam `IReviewGuidelines` (same pattern as `IReviewMemory`): `DistillingReviewGuidelines` (Infrastructure) collects doc sources from the existing shared workspace checkout, hash-caches one profile blob per project in a new `ProjectGuidelinesEntity` (zero LLM calls while docs are unchanged), and respects human curation (`ManuallyEdited` blocks auto-refresh). `PromptBuilder` renders the profile as an authoritative section before the memory section and gains altitude/security instructions in `DefaultSystemPrompt`. A small JSON API + a card on the existing Memory page expose view/edit/re-distill.

**Tech Stack:** .NET 10, EF Core (SQLite/Postgres, hand-neutralized migrations), MEAI `IChatClient`, ASP.NET Minimal API, React + TanStack Query (frontend), xUnit.

**Spec:** `docs/superpowers/specs/2026-07-18-architecture-profile-design.md` — the authoritative requirements. Branch: `feat/architecture-profile` (based on main).

## Global Constraints

- Solution file is `Naudit.slnx` (XML format) — `dotnet build Naudit.slnx`; single test class: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter <Name>`.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. `IReviewGuidelines` + `ReviewGuidelinesOptions` live in Core (no SDKs); `DistillingReviewGuidelines`, entity, endpoints live in Infrastructure/Web.
- Code comments German; docs (`docs/`, plan-facing text in files) English where the file is English.
- **Fail-open philosophy** (audit-sink style): a guidelines error never fails or blocks a review; real cancellation propagates (`when (!ct.IsCancellationRequested)`).
- Migrations are **hand-kept provider-neutral**: no explicit column types in `Up()`, both `Sqlite:Autoincrement` and `Npgsql:ValueGenerationStrategy` annotations; Designer files carry **no** `HasColumnType`; the snapshot stays SQLite-baked. There is **no dotnet-ef tool** in this environment — migration, Designer, and snapshot are written by hand from the in-repo templates named in Task 2. The WAF tests run `Database.Migrate()` and EF throws `PendingModelChangesWarning` on model≠snapshot — they are the migration oracle.
- The distillation call uses the **global** `IChatClient` directly (never `IAiClientRouter`).
- Config keys: `Naudit:Review:Guidelines:Enabled` (default `true`), `:MaxSourceChars` (default `60000`), `:MaxProfileChars` (default `4000`); `Sources` list stays env-only.
- Exact profile-section heading: `# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)`.
- TDD: red → green → one commit per task.

---

### Task 1: PromptBuilder — guidelines section + altitude/security system prompt

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs` (note the historic filename typo; class is `PromptBuilder`)
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs` (append)

**Interfaces:**
- Consumes: nothing new.
- Produces: `PromptBuilder.Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes, IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null, IReadOnlyList<MemoryEntry>? memory = null, bool toolsAvailable = false, string? guidelines = null)` — the new **trailing optional** `guidelines` parameter (Task 4 passes it). Extended `DefaultSystemPrompt` const.

- [ ] **Step 1: Write the failing tests** (append to `PromtBuilderTests.cs`; the tests are self-contained — no helpers from the existing file are required):

```csharp
    [Fact]
    public void Build_withGuidelines_rendersAuthoritativeSection_beforeMemory()
    {
        var request = new ReviewRequest("7", 1, "Titel");
        var changes = new List<CodeChange> { new("src/A.cs", "@@ -0,0 +1 @@\n+x") };
        var memory = new List<MemoryEntry> { new(MemoryKind.Convention, null, "Konvention X", null) };

        var messages = PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes,
            memory: memory, guidelines: "- Webhook endpoints must enqueue and return 200 immediately.");
        var text = string.Join("\n", messages.Select(m => m.Text));

        Assert.Contains("# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)", text);
        Assert.Contains("Webhook endpoints must enqueue", text);
        // Guidelines-Sektion steht VOR der Memory-Sektion (Memory bleibt zuletzt).
        Assert.True(text.IndexOf("# Project guidelines", StringComparison.Ordinal)
                  < text.IndexOf("# Project memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_withoutGuidelines_isByteIdentical()
    {
        var request = new ReviewRequest("7", 1, "Titel");
        var changes = new List<CodeChange> { new("src/A.cs", "@@ -0,0 +1 @@\n+x") };

        var without = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes).Select(m => m.Text));
        var withNull = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes, guidelines: null).Select(m => m.Text));
        var withBlank = string.Join("\n", PromptBuilder.Build(PromptBuilder.DefaultSystemPrompt, request, changes, guidelines: "   ").Select(m => m.Text));

        Assert.Equal(without, withNull);
        Assert.Equal(without, withBlank);
    }

    [Fact]
    public void DefaultSystemPrompt_containsAltitudeAndSecurityInstructions()
    {
        Assert.Contains("architecture level", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("Project guidelines", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("injection surfaces", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("omit \"line\"", PromptBuilder.DefaultSystemPrompt);
    }
```

Add `using Naudit.Core.Models;` / `using System.Linq;` to the test file only if missing.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: FAIL — no `guidelines` parameter (compile error) / missing prompt phrases.

- [ ] **Step 3a: Extend `DefaultSystemPrompt`.** Append to the existing const (after the final `"…not as issues to flag."` segment, keeping the string-concat style):

```csharp
        "A read-only \"Project guidelines\" section may follow - it contains the project's own architecture and convention rules, " +
        "distilled from its documentation and curated by maintainers: treat them as authoritative and report violations of them as findings. " +
        "Also review the change at the architecture level: breaks of contracts or patterns the codebase itself establishes, and layering violations. " +
        "Such findings often map to no single changed line - report them without a line (omit \"line\") rather than dropping them. " +
        "For security, specifically check: new endpoints or handlers for missing authentication or authorization; " +
        "injection surfaces (SQL, command, path, SSRF); secrets or tokens in code or logs; and unsafe deserialization.";
```

- [ ] **Step 3b: Add the section renderer and the `guidelines` parameter.** Extend the `Build` signature with trailing `string? guidelines = null`; in the render sequence insert between `AppendToolGuidance(sb, toolsAvailable);` and `AppendMemory(sb, memory);`:

```csharp
        AppendGuidelines(sb, guidelines);
```

And add (next to `AppendMemory`):

```csharp
    // Architektur-Profil: destillierte, maintainer-kuratierte Projekt-Guidelines — autoritativ,
    // direkt vor dem Memory (beide tragen Maintainer-Entscheidungen; Memory bleibt zuletzt).
    private static void AppendGuidelines(StringBuilder sb, string? guidelines)
    {
        if (string.IsNullOrWhiteSpace(guidelines))
            return;
        sb.AppendLine();
        sb.AppendLine("# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)");
        sb.AppendLine();
        sb.AppendLine(guidelines.Trim());
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: PASS (new + all existing PromptBuilder tests — the byte-identical guarantee protects them).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(guidelines): Prompt-Sektion Project guidelines + Altitude-/Security-Anweisungen im System-Prompt"
```

---

### Task 2: `ProjectGuidelinesEntity` + DbContext + hand-neutral migration

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs` (append entity)
- Modify: `src/Naudit.Infrastructure/Data/NauditDbContext.cs` (DbSet + model config)
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260718150000_AddProjectGuidelines.cs`
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260718150000_AddProjectGuidelines.Designer.cs`
- Modify: `src/Naudit.Infrastructure/Data/Migrations/NauditDbContextModelSnapshot.cs`
- Test: `tests/Naudit.Tests/ProjectGuidelinesEntityTests.cs`

**Interfaces:**
- Consumes: existing `ProjectEntity`.
- Produces: `ProjectGuidelinesEntity { int Id; int ProjectId; ProjectEntity Project; string Markdown; string SourceHash; DateTime DistilledAt; bool ManuallyEdited; DateTime? SourcesChangedAt; string UpdatedBy }`, `NauditDbContext.ProjectGuidelines` DbSet, unique index on `ProjectId`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/ProjectGuidelinesEntityTests.cs
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ProjectGuidelinesEntityTests`
Expected: FAIL — entity/DbSet missing (compile error).

- [ ] **Step 3a: Append the entity to `Entities.cs`:**

```csharp
/// <summary>Architektur-Profil eines Projekts: destillierte Projekt-Guidelines als EIN Blob.
/// Auto-Refresh (Neu-Destillieren bei Doku-Änderung) läuft nur, solange nicht manuell editiert —
/// menschliche Kuration gewinnt; SourcesChangedAt trägt dann das Stale-Signal für die WebUI.</summary>
public sealed class ProjectGuidelinesEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ProjectEntity Project { get; set; } = null!;
    public required string Markdown { get; set; }
    public required string SourceHash { get; set; }    // SHA256 über die destillierten Quellinhalte
    public DateTime DistilledAt { get; set; }
    public bool ManuallyEdited { get; set; }           // WebUI-Edit ⇒ Auto-Refresh stoppt
    public DateTime? SourcesChangedAt { get; set; }    // Quellen geändert, Refresh blockiert ⇒ Stale-Hinweis
    public required string UpdatedBy { get; set; }     // Editor-Username bzw. "naudit" für Destillate
}
```

- [ ] **Step 3b: DbContext.** Add `public DbSet<ProjectGuidelinesEntity> ProjectGuidelines => Set<ProjectGuidelinesEntity>();` next to the other DbSets, and in `OnModelCreating` (next to the `MemoryEntryEntity` block):

```csharp
        b.Entity<ProjectGuidelinesEntity>(e =>
        {
            e.HasIndex(x => x.ProjectId).IsUnique();     // genau ein Profil pro Projekt
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 3c: Hand-written migration** (`20260718150000_AddProjectGuidelines.cs`, provider-neutral — the in-repo template is `20260717212803_AddMemoryEntries.cs`):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AddMemoryEntries bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).
    /// <inheritdoc />
    public partial class AddProjectGuidelines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectGuidelines",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(nullable: false),
                    Markdown = table.Column<string>(nullable: false),
                    SourceHash = table.Column<string>(nullable: false),
                    DistilledAt = table.Column<DateTime>(nullable: false),
                    ManuallyEdited = table.Column<bool>(nullable: false),
                    SourcesChangedAt = table.Column<DateTime>(nullable: true),
                    UpdatedBy = table.Column<string>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectGuidelines", x => x.Id);
                    table.ForeignKey("FK_ProjectGuidelines_Projects_ProjectId", x => x.ProjectId,
                        principalTable: "Projects", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex("IX_ProjectGuidelines_ProjectId", "ProjectGuidelines", "ProjectId", unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable(name: "ProjectGuidelines");
    }
}
```

- [ ] **Step 3d: Designer + snapshot (hand-edit; the oracle is the test suite).**
  - **Snapshot** (`NauditDbContextModelSnapshot.cs`): add a `ProjectGuidelinesEntity` property block in the entity section (alphabetically after `ProjectEntity`) **with** SQLite `HasColumnType` values, exactly mirroring the `MemoryEntryEntity` block's style (`int`/`bool` → `"INTEGER"`, `string`/`DateTime` → `"TEXT"`; `Markdown`, `SourceHash`, `UpdatedBy` get `.IsRequired()`); `HasKey("Id")`, `HasIndex("ProjectId").IsUnique()`, `ToTable("ProjectGuidelines")`. In the relationships section (second block for each entity, around line 325+), add the FK block mirroring `MemoryEntryEntity`'s Project relationship: `HasOne("Naudit.Infrastructure.Data.ProjectEntity", "Project").WithMany().HasForeignKey("ProjectId").OnDelete(DeleteBehavior.Cascade).IsRequired()` + `Navigation("Project")`.
  - **Designer** (`…AddProjectGuidelines.Designer.cs`): follow the in-repo template `20260717230852_AddFindingCommentIds.Designer.cs` **exactly** for attribute header style (`[DbContext(typeof(NauditDbContext))]`, `[Migration("20260718150000_AddProjectGuidelines")]`) and for whether property lines carry `HasColumnType` — per CLAUDE.md the Designers are neutralized (no `HasColumnType`); replicate whatever the template actually does. Content = the full current model **including** the new entity (i.e., the snapshot content in the Designer's style).

- [ ] **Step 4: Run the focused test AND the migration oracle**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ProjectGuidelinesEntityTests|WebhookEndpointTests"`
Expected: PASS. `WebhookEndpointTests` boots the host → `DbSettingsLoader` runs `Database.Migrate()` → a model≠snapshot mismatch throws `PendingModelChangesWarning` and fails these tests; green means the hand-edit is consistent.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Data/Entities.cs src/Naudit.Infrastructure/Data/NauditDbContext.cs src/Naudit.Infrastructure/Data/Migrations/ tests/Naudit.Tests/ProjectGuidelinesEntityTests.cs
git commit -m "feat(guidelines): ProjectGuidelinesEntity + Migration AddProjectGuidelines (ein Profil pro Projekt)"
```

---

### Task 3: Core seam + options + `DistillingReviewGuidelines`

**Files:**
- Create: `src/Naudit.Core/Abstractions/IReviewGuidelines.cs`
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs` (add `Guidelines` + options class)
- Create: `src/Naudit.Infrastructure/Guidelines/NullReviewGuidelines.cs`
- Create: `src/Naudit.Infrastructure/Guidelines/DistillingReviewGuidelines.cs`
- Test: `tests/Naudit.Tests/DistillingReviewGuidelinesTests.cs`

**Interfaces:**
- Consumes: `ProjectGuidelinesEntity`/`NauditDbContext` (Task 2), `IChatClient` (MEAI), `IPromptRedactor` (existing Core seam).
- Produces (Tasks 4/5 depend on these exact shapes):
  - `interface IReviewGuidelines { Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default); }` (namespace `Naudit.Core.Abstractions`)
  - `ReviewOptions.Guidelines` of type `ReviewGuidelinesOptions { bool Enabled = true; int MaxSourceChars = 60_000; int MaxProfileChars = 4_000; List<string> Sources = ["CLAUDE.md","AGENTS.md","README.md","CONTRIBUTING.md","docs/**/*.md"] }` (namespace `Naudit.Core.Review`)
  - `DistillingReviewGuidelines(NauditDbContext db, IChatClient chatClient, IPromptRedactor redactor, ReviewOptions options, ILogger<DistillingReviewGuidelines> logger)`

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Naudit.Tests/DistillingReviewGuidelinesTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Guidelines;
using Naudit.Infrastructure.Redaction;
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
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DistillingReviewGuidelinesTests`
Expected: FAIL — types missing (compile error).

- [ ] **Step 3a: Core seam** (`src/Naudit.Core/Abstractions/IReviewGuidelines.cs`):

```csharp
namespace Naudit.Core.Abstractions;

/// <summary>Liefert das Architektur-Profil (destillierte Projekt-Guidelines) für ein Review.
/// workspaceDir = der geteilte Checkout (null, wenn keiner stattfand) — die Implementierung
/// destilliert daraus bzw. liefert das gespeicherte Profil. Fail-open lebt in der Implementierung.</summary>
public interface IReviewGuidelines
{
    Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default);
}
```

- [ ] **Step 3b: Options.** In `ReviewOptions.cs` add to `ReviewOptions`:

```csharp
    /// <summary>Architektur-Profil: destillierte Projekt-Guidelines (Naudit:Review:Guidelines).</summary>
    public ReviewGuidelinesOptions Guidelines { get; set; } = new();
```

and append the class:

```csharp
/// <summary>Architektur-Profil. Default AN; Enabled=false ⇒ NullReviewGuidelines (heutiges Verhalten).</summary>
public sealed class ReviewGuidelinesOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Deckel für den Destillat-Input (Summe der Quelldateien; übergroße Dateien werden ganz übersprungen).</summary>
    public int MaxSourceChars { get; set; } = 60_000;

    /// <summary>Deckel für das gespeicherte/eingespeiste Profil.</summary>
    public int MaxProfileChars { get; set; } = 4_000;

    /// <summary>Quellen relativ zum Repo-Root; Reihenfolge = Priorität. Exakte Namen oder das Muster "dir/**/*.md".</summary>
    public List<string> Sources { get; set; } =
        ["CLAUDE.md", "AGENTS.md", "README.md", "CONTRIBUTING.md", "docs/**/*.md"];
}
```

- [ ] **Step 3c: Null impl** (`src/Naudit.Infrastructure/Guidelines/NullReviewGuidelines.cs`):

```csharp
using Naudit.Core.Abstractions;

namespace Naudit.Infrastructure.Guidelines;

/// <summary>No-Op (Feature aus): nie ein Profil — Prompt bleibt byte-identisch zu heute.</summary>
public sealed class NullReviewGuidelines : IReviewGuidelines
{
    public Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
```

- [ ] **Step 3d: The distiller** (`src/Naudit.Infrastructure/Guidelines/DistillingReviewGuidelines.cs`):

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Guidelines;

/// <summary>Destilliert aus der Repo-Doku (CLAUDE.md, AGENTS.md, README, docs/…) ein kompaktes
/// Architektur-Profil und hält es hash-gecacht in der DB: unveränderte Doku ⇒ NULL LLM-Calls.
/// Menschliche Kuration gewinnt (ManuallyEdited blockt Auto-Refresh; SourcesChangedAt = Stale-Signal).
/// Fail-open: jeder Fehler ⇒ gespeichertes Profil bzw. null, geloggt — kippt nie das Review.</summary>
public sealed class DistillingReviewGuidelines(
    NauditDbContext db, IChatClient chatClient, IPromptRedactor redactor,
    ReviewOptions options, ILogger<DistillingReviewGuidelines> logger) : IReviewGuidelines
{
    private const string DistillSystemPrompt =
        "You extract binding project rules for a code reviewer. From the repository documentation below, " +
        "extract the 10-20 binding architecture and convention rules of this project as a terse Markdown bullet list " +
        "(one rule per bullet, imperative, no headings, no commentary). " +
        "Only include rules actually stated in the documentation - never invent rules. " +
        "Prefer rules a code review can enforce: layering and dependency direction, endpoint or API contracts, " +
        "error-handling policies, security requirements, naming and testing conventions. " +
        "If the documentation contains no such rules, respond with an empty string.";

    public async Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
    {
        try
        {
            var project = await db.Projects.SingleOrDefaultAsync(p => p.PlatformProjectId == projectId, ct);
            var stored = project is null
                ? null
                : await db.ProjectGuidelines.SingleOrDefaultAsync(g => g.ProjectId == project.Id, ct);

            if (workspaceDir is null)
                return Emit(stored?.Markdown);   // kein Checkout ⇒ gespeichertes Profil (oder nichts)

            var sources = await CollectSourcesAsync(workspaceDir, ct);
            if (sources.Count == 0)
                return Emit(stored?.Markdown);

            var hash = ComputeHash(sources);
            if (stored is not null && stored.SourceHash == hash)
                return Emit(stored.Markdown);    // unveränderte Doku ⇒ kein LLM-Call

            if (stored is not null && stored.ManuallyEdited)
            {
                // Menschliche Kuration gewinnt: nie überschreiben, nur das Stale-Signal setzen.
                if (stored.SourcesChangedAt is null)
                {
                    stored.SourcesChangedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
                return Emit(stored.Markdown);
            }

            string profile;
            try
            {
                profile = await DistillAsync(sources, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Guidelines-Destillat für {Project} fehlgeschlagen — nutze gespeichertes Profil.", projectId);
                return Emit(stored?.Markdown);
            }

            if (project is null)
            {
                // Allererstes Review: die Projekt-Zeile (inkl. Ownership) legt erst der Audit-Sink
                // NACH dem Review an — Profil liefern, aber nicht speichern (FK-Ziel fehlt bewusst).
                return Emit(profile);
            }

            if (stored is null)
            {
                stored = new ProjectGuidelinesEntity
                {
                    ProjectId = project.Id, Markdown = profile, SourceHash = hash,
                    DistilledAt = DateTime.UtcNow, UpdatedBy = "naudit",
                };
                db.ProjectGuidelines.Add(stored);
            }
            else
            {
                stored.Markdown = profile;
                stored.SourceHash = hash;
                stored.DistilledAt = DateTime.UtcNow;
                stored.SourcesChangedAt = null;
                stored.UpdatedBy = "naudit";
            }
            await db.SaveChangesAsync(ct);       // auch ein LEERES Destillat speichern: der Hash verhindert Re-Destillieren
            return Emit(profile);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Guidelines-Abruf für {Project} fehlgeschlagen — Review läuft ohne Profil weiter.", projectId);
            return null;
        }
    }

    // Leeres/Whitespace-Profil ⇒ null (PromptBuilder lässt die Sektion dann weg).
    private static string? Emit(string? markdown)
        => string.IsNullOrWhiteSpace(markdown) ? null : markdown;

    private async Task<string> DistillAsync(IReadOnlyList<(string Path, string Content)> sources, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in sources)
        {
            sb.AppendLine($"## {path}");
            sb.AppendLine(await redactor.RedactAsync(content, ct));   // Quellen wie jeden Prompt-Bestandteil maskieren
            sb.AppendLine();
        }

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DistillSystemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
        var response = await chatClient.GetResponseAsync(messages, new ChatOptions(), ct);
        var profile = response.Text.Trim();
        var cap = options.Guidelines.MaxProfileChars;
        return profile.Length <= cap ? profile : profile[..cap];
    }

    // Deterministische Sammlung (stabile Reihenfolge ⇒ stabiler Hash): exakte Namen in Sources-Reihenfolge,
    // "dir/**/*.md"-Muster rekursiv sortiert. Dateien, die das Restbudget sprengen, werden GANZ übersprungen.
    private async Task<List<(string Path, string Content)>> CollectSourcesAsync(string root, CancellationToken ct)
    {
        var result = new List<(string, string)>();
        var budget = options.Guidelines.MaxSourceChars;

        foreach (var pattern in options.Guidelines.Sources)
        {
            foreach (var file in ResolvePattern(root, pattern))
            {
                if (!File.Exists(file))
                    continue;
                var content = await File.ReadAllTextAsync(file, ct);
                if (content.Length > budget)
                    continue;
                budget -= content.Length;
                result.Add((Path.GetRelativePath(root, file).Replace('\\', '/'), content));
            }
        }
        return result;
    }

    private static IEnumerable<string> ResolvePattern(string root, string pattern)
    {
        const string recursiveMd = "/**/*.md";
        if (pattern.EndsWith(recursiveMd, StringComparison.Ordinal))
        {
            var dir = Path.Combine(root, pattern[..^recursiveMd.Length]);
            if (!Directory.Exists(dir))
                return [];
            return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.Ordinal);
        }
        return [Path.Combine(root, pattern)];
    }

    private static string ComputeHash(IReadOnlyList<(string Path, string Content)> sources)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in sources)
        {
            sb.Append(path).Append('\n').Append(content).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
```

Note for the implementer: `NullPromptRedactor` lives in `src/Naudit.Infrastructure/Redaction/` (check its namespace for the test's `using`). `ChatResponse.Text` is the MEAI GA API (see CLAUDE.md — a missing-member error means package mismatch, not logic).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DistillingReviewGuidelinesTests`
Expected: PASS (all 10).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Abstractions/IReviewGuidelines.cs src/Naudit.Core/Review/ReviewOptions.cs src/Naudit.Infrastructure/Guidelines/ tests/Naudit.Tests/DistillingReviewGuidelinesTests.cs
git commit -m "feat(guidelines): IReviewGuidelines-Seam + DistillingReviewGuidelines (hash-gecacht, kuratierbar, fail-open)"
```

---

### Task 4: ReviewService wiring + DI + SettingsCatalog

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeReviewGuidelines.cs`
- Modify: every existing `new ReviewService(` call site in `tests/` (grep for it — at least `ReviewServiceTests`, `ReviewAuditSinkTests`, `ReviewMemoryWiringTests`)
- Test: `tests/Naudit.Tests/ReviewGuidelinesWiringTests.cs`

**Interfaces:**
- Consumes: `IReviewGuidelines` (Task 3), `PromptBuilder.Build(..., string? guidelines)` (Task 1), `IReviewWorkspace.RootPath` (existing).
- Produces: `ReviewService` ctor gains trailing `IReviewGuidelines reviewGuidelines`; `GatherGroundingAsync` returns `(IReadOnlyList<ScanFinding> Findings, ReviewContext Context, string? Guidelines)`.

- [ ] **Step 1: Create the fake and write the failing wiring test.**

```csharp
// tests/Naudit.Tests/Fakes/FakeReviewGuidelines.cs
using Naudit.Core.Abstractions;

namespace Naudit.Tests.Fakes;

/// <summary>Liefert ein festes Profil (oder null) und protokolliert die Aufruf-Argumente.</summary>
internal sealed class FakeReviewGuidelines(string? profile) : IReviewGuidelines
{
    public string? LastProjectId { get; private set; }
    public string? LastWorkspaceDir { get; private set; }

    public Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default)
    {
        LastProjectId = projectId;
        LastWorkspaceDir = workspaceDir;
        return Task.FromResult(profile);
    }
}
```

For `ReviewGuidelinesWiringTests.cs`: copy the structure of the existing `tests/Naudit.Tests/ReviewMemoryWiringTests.cs` (same fakes, same `ReviewService` construction) and adapt it to guidelines. Two tests:

1. `ReviewAsync_rendersGuidelinesSection_inPrompt` — construct `ReviewService` exactly like the memory wiring test does, but pass `new FakeReviewGuidelines("- Webhook endpoints must enqueue and return 200 immediately.")` as the new last ctor argument; run `ReviewAsync`; assert via the test's `FakeChatClient.LastMessages` that the joined prompt contains `"# Project guidelines"` **and** `"must enqueue and return 200"` (this is the end-to-end litmus test).
2. `ReviewAsync_withNullGuidelines_promptHasNoGuidelinesSection` — same setup with `new FakeReviewGuidelines(null)`; assert the joined prompt does **not** contain `"# Project guidelines"`.

Also add `new FakeReviewGuidelines(null)` (or `NullReviewGuidelines`) as the new trailing argument at **every** existing `new ReviewService(` call site so the solution compiles (`grep -rn "new ReviewService(" tests/`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet build Naudit.slnx`
Expected: FAIL — `ReviewService` has no `reviewGuidelines` parameter yet (the wiring test doesn't compile).

- [ ] **Step 3a: `ReviewService`.** Add trailing ctor parameter `IReviewGuidelines reviewGuidelines`. Change `GatherGroundingAsync` to return `(IReadOnlyList<ScanFinding> Findings, ReviewContext Context, string? Guidelines)`:
  - `needCheckout == false` path: `return ([], ReviewContext.Empty, await reviewGuidelines.GetAsync(request.ProjectId, null, ct));`
  - checkout-failure catch: `return ([], ReviewContext.Empty, await reviewGuidelines.GetAsync(request.ProjectId, null, ct));`
  - inside `await using (workspace)`, after context collection: `var guidelines = await reviewGuidelines.GetAsync(request.ProjectId, workspace.RootPath, ct);` and return it in the tuple.

  In `ReviewAsync`: `var (findings, context, guidelines) = await GatherGroundingAsync(...)`; after the memory redaction block add:

```csharp
        // Architektur-Profil läuft — wie alles — vor dem Prompt durch den Redactor.
        var redGuidelines = guidelines is null ? null : await redactor.RedactAsync(guidelines, ct);
```

  and extend the Build call: `PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext, redMemory, toolsAvailable: tools.Count > 0, guidelines: redGuidelines);`

- [ ] **Step 3b: DI.** In `DependencyInjection.cs`, directly after the `IReviewMemory` registration block:

```csharp
        // Architektur-Profil: destillierte Guidelines aus der Repo-Doku (Naudit:Review:Guidelines).
        // Aus ⇒ NullReviewGuidelines (immer null) = heutiges Prompt-Verhalten.
        if (reviewOptions.Guidelines.Enabled)
            services.AddScoped<IReviewGuidelines, DistillingReviewGuidelines>();
        else
            services.AddSingleton<IReviewGuidelines, NullReviewGuidelines>();
```

Add `using Naudit.Infrastructure.Guidelines;` if missing.

- [ ] **Step 3c: SettingsCatalog.** After the `Naudit:Review:Memory:MaxEntries` line:

```csharp
        new("Naudit:Review:Guidelines:Enabled", false),
        new("Naudit:Review:Guidelines:MaxSourceChars", false),
        new("Naudit:Review:Guidelines:MaxProfileChars", false),
```

- [ ] **Step 4: Run tests to verify green**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ReviewGuidelinesWiringTests|ReviewServiceTests|ReviewMemoryWiringTests|SettingsEndpointTests"`
Expected: PASS. Then `dotnet build Naudit.slnx` — 0 errors (all call sites updated).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/
git commit -m "feat(guidelines): ReviewService speist das Architektur-Profil in den Prompt (Checkout-geteilt, redacted) + DI/Settings"
```

---

### Task 5: Guidelines API endpoints

**Files:**
- Create: `src/Naudit.Web/Endpoints/GuidelinesEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (add `app.MapGuidelinesEndpoints();` next to `app.MapMemoryEndpoints();`)
- Test: `tests/Naudit.Tests/GuidelinesEndpointTests.cs`

**Interfaces:**
- Consumes: `ProjectGuidelinesEntity` (Task 2), `ReviewGuidelinesOptions.MaxProfileChars` (Task 3), existing `CurrentAccount.GetActiveAsync` / `CanSeeProjectAsync` helpers (see `MemoryEndpoints.cs`).
- Produces:
  - `GET  /api/projects/{id:int}/guidelines` → `200 { markdown, distilledAt, manuallyEdited, sourcesChangedAt, updatedBy }` (all-null payload when no row), `404` unknown project, `403` not visible.
  - `PUT  /api/projects/{id:int}/guidelines` body `{ "markdown": "..." }` → upsert; sets `ManuallyEdited=true`, `SourcesChangedAt=null`, `UpdatedBy` = session user; `400` empty or over `MaxProfileChars`.
  - `POST /api/projects/{id:int}/guidelines/redistill` → resets `ManuallyEdited=false`, `SourceHash=""`, `SourcesChangedAt=null` (profile refreshes on the next review); idempotent `200 { pending: true }` (also when no row exists).

- [ ] **Step 1: Write the failing tests.** Model `GuidelinesEndpointTests.cs` on the existing `MemoryEndpointTests.cs` — same `TestAppFactory` usage, same login/seed helpers (copy that file's setup verbatim and adapt). Cover:
  - `Get_withoutSession_returns401`
  - `Get_unknownProject_returns404`
  - `Get_foreignProject_returns403` (mirror the memory tests' non-owner setup)
  - `Put_thenGet_roundtrips_andMarksManuallyEdited` (PUT markdown, GET returns it with `manuallyEdited: true`, `updatedBy` = username)
  - `Put_emptyOrOversized_returns400` (empty string; and a string longer than 4000 chars)
  - `Redistill_resetsCurationFlags` (seed a row with `ManuallyEdited=true, SourceHash="h"` via the factory's DbContext, POST redistill, assert via DbContext: `ManuallyEdited=false`, `SourceHash=""`, `SourcesChangedAt=null`)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GuidelinesEndpointTests`
Expected: FAIL — 404 for unmapped routes / compile error.

- [ ] **Step 3: Implement `GuidelinesEndpoints.cs`:**

```csharp
using Microsoft.EntityFrameworkCore;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Architektur-Profil-API: destillierte Guidelines je Projekt einsehen, kuratieren,
/// Neu-Destillieren anstoßen. Sichtbarkeit wie das Dashboard, 401/403 statt Redirects.</summary>
public static class GuidelinesEndpoints
{
    private sealed record PutBody(string? Markdown);

    public static void MapGuidelinesEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/projects/{id:int}/guidelines", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            return Results.Ok(new
            {
                markdown = g?.Markdown,
                distilledAt = g?.DistilledAt,
                manuallyEdited = g?.ManuallyEdited ?? false,
                sourcesChangedAt = g?.SourcesChangedAt,
                updatedBy = g?.UpdatedBy,
            });
        });

        api.MapPut("/projects/{id:int}/guidelines", async (HttpContext ctx, NauditDbContext db, int id, PutBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            // Recovery-sicher: ReviewOptions kommt aus AddNauditInfrastructure und fehlt im Recovery-Modus.
            var cap = (ctx.RequestServices.GetService<ReviewOptions>() ?? new ReviewOptions()).Guidelines.MaxProfileChars;
            var markdown = body.Markdown?.Trim();
            if (string.IsNullOrEmpty(markdown))
                return Results.BadRequest(new { error = "markdown must not be empty" });
            if (markdown.Length > cap)
                return Results.BadRequest(new { error = $"markdown must not exceed {cap} characters" });
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            if (g is null)
            {
                g = new ProjectGuidelinesEntity
                {
                    ProjectId = id, Markdown = markdown, SourceHash = "",
                    DistilledAt = DateTime.UtcNow, UpdatedBy = acct.Username,
                };
                db.ProjectGuidelines.Add(g);
            }
            else
            {
                g.Markdown = markdown;
                g.UpdatedBy = acct.Username;
            }
            g.ManuallyEdited = true;          // Kuration gewinnt: Auto-Refresh stoppt ab jetzt
            g.SourcesChangedAt = null;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { manuallyEdited = true });
        });

        api.MapPost("/projects/{id:int}/guidelines/redistill", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            // Kein Inline-LLM-Call (die WebUI hat keinen Checkout): Flags zurücksetzen — der
            // geleerte Hash matcht nie, das nächste Review destilliert frisch.
            var g = await db.ProjectGuidelines.SingleOrDefaultAsync(x => x.ProjectId == id, ctx.RequestAborted);
            if (g is not null)
            {
                g.ManuallyEdited = false;
                g.SourceHash = "";
                g.SourcesChangedAt = null;
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            return Results.Ok(new { pending = true });
        });
    }
}
```

Map it in `Program.cs` next to `app.MapMemoryEndpoints();`: `app.MapGuidelinesEndpoints();`

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GuidelinesEndpointTests|MemoryEndpointTests"`
Expected: PASS (new endpoints + memory routes unregressed).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/GuidelinesEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/GuidelinesEndpointTests.cs
git commit -m "feat(guidelines): Profil-API (GET/PUT/redistill) mit Memory-Routen-Autorisierung"
```

---

### Task 6: WebUI — architecture-profile card on the Memory page

**Files:**
- Modify: `src/frontend/src/hooks/queries.ts` (add `useProjectGuidelines` + DTO import)
- Modify: `src/frontend/src/hooks/mutations.ts` (add `useSaveGuidelines`, `useRedistillGuidelines`)
- Modify: the frontend types module that exports `ProjectMemoryDto` (follow its import in `queries.ts`; add `ProjectGuidelinesDto`)
- Modify: `src/frontend/src/components/pages/MemoryPage.tsx` (card above the convention-create row)

**Interfaces:**
- Consumes: Task 5's API shapes.
- Produces: `ProjectGuidelinesDto { markdown: string | null; distilledAt: string | null; manuallyEdited: boolean; sourcesChangedAt: string | null; updatedBy: string | null }`.

- [ ] **Step 1: Types + hooks** (mirror the existing `useProjectMemory` / `useCreateConvention` idioms exactly — same `api<T>()` helper, same queryKey/invalidations):

```typescript
// queries.ts
export function useProjectGuidelines(projectId: number | null) {
  return useQuery({
    queryKey: ["guidelines", projectId],
    queryFn: () => api<ProjectGuidelinesDto>(`/api/projects/${projectId}/guidelines`),
    enabled: projectId !== null,
  });
}

// mutations.ts
export function useSaveGuidelines(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { markdown: string }) =>
      api<{ manuallyEdited: boolean }>(`/api/projects/${projectId}/guidelines`, {
        method: "PUT",
        body: JSON.stringify({ markdown: vars.markdown }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["guidelines", projectId] }),
  });
}

export function useRedistillGuidelines(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () =>
      api<{ pending: boolean }>(`/api/projects/${projectId}/guidelines/redistill`, { method: "POST" }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["guidelines", projectId] }),
  });
}
```

- [ ] **Step 2: The card in `MemoryPage.tsx`** — insert between the header row and the convention-create row; reuse the page's existing Tailwind tokens (`border-border bg-elev text-ink…`):

```tsx
      {/* Architektur-Profil (destillierte Guidelines) */}
      <GuidelinesCard projectId={selected} />
```

with a `GuidelinesCard` component in the same file:

```tsx
function GuidelinesCard({ projectId }: { projectId: number | null }) {
  const { data, isLoading } = useProjectGuidelines(projectId);
  const save = useSaveGuidelines(projectId);
  const redistill = useRedistillGuidelines(projectId);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");

  if (isLoading || !data) return null;

  const startEdit = () => { setDraft(data.markdown ?? ""); setEditing(true); };
  const submit = () => {
    const t = draft.trim();
    if (!t || save.isPending) return;
    save.mutate({ markdown: t }, { onSuccess: () => setEditing(false) });
  };

  return (
    <div className="rounded-xl border border-border bg-elev p-4">
      <div className="mb-2 flex items-center gap-2">
        <h2 className="text-[13.5px] font-semibold text-ink">Architecture profile</h2>
        <span className="font-mono text-[10.5px] text-ink3">
          {data.markdown
            ? `${data.manuallyEdited ? "curated" : "distilled"} · ${data.updatedBy ?? ""}${data.distilledAt ? ` · ${new Date(data.distilledAt).toLocaleDateString()}` : ""}`
            : "distills from the repo's docs on the next review"}
        </span>
        <div className="ml-auto flex gap-2">
          {!editing && data.markdown && (
            <button className="rounded px-1.5 py-0.5 font-mono text-[10px] text-ink3 hover:text-ink" onClick={startEdit}>edit</button>
          )}
          {!editing && (
            <button
              className="rounded px-1.5 py-0.5 font-mono text-[10px] text-ink3 hover:text-ink disabled:opacity-50"
              disabled={redistill.isPending}
              title="Discards manual edits; the profile is re-distilled on the next review."
              onClick={() => { if (window.confirm("Re-distill from repository docs on the next review? Manual edits are discarded.")) redistill.mutate(); }}
            >
              re-distill
            </button>
          )}
        </div>
      </div>
      {data.sourcesChangedAt && (
        <div className="mb-2 font-mono text-[11px] text-warn">
          Repository docs changed since this profile was curated — “re-distill” to rebuild it.
        </div>
      )}
      {!editing && data.markdown && (
        <pre className="whitespace-pre-wrap font-mono text-[12px] leading-snug text-ink2">{data.markdown}</pre>
      )}
      {editing && (
        <div className="flex flex-col gap-2">
          <textarea
            className="min-h-[10rem] rounded-lg border border-border bg-elev p-2.5 font-mono text-[12px] text-ink"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
          />
          <div className="flex gap-2">
            <button
              className="rounded-lg bg-acc/12 px-3 py-1.5 text-[13px] font-semibold text-acc disabled:opacity-50"
              disabled={!draft.trim() || save.isPending}
              onClick={submit}
            >
              Save
            </button>
            <button className="rounded-lg px-3 py-1.5 text-[13px] text-ink3 hover:text-ink" onClick={() => setEditing(false)}>Cancel</button>
          </div>
        </div>
      )}
    </div>
  );
}
```

Import the new hooks at the top of `MemoryPage.tsx`.

- [ ] **Step 3: Verify**

Run: `cd src/frontend && npm ci && npm run lint && npm run build`
Expected: lint clean, `tsc --noEmit` + vite build succeed.

- [ ] **Step 4: Commit**

```bash
git add src/frontend/src
git commit -m "feat(guidelines): Architektur-Profil-Karte auf der Memory-Seite (anzeigen, kuratieren, re-distill)"
```

---

### Task 7: Docs + full-suite gate

**Files:**
- Create: `docs/review-guidelines.md`
- Modify: `CLAUDE.md` (extension-points list + request-flow sentence)

- [ ] **Step 1: Write `docs/review-guidelines.md`** (English). Sections: **What it is** (auto-distilled architecture profile from the repo's own docs, fed into every review prompt as an authoritative section; motivates with the architecture-altitude gap); **How distillation works** (source list + caps, SHA256 hash cache ⇒ zero LLM calls while docs unchanged, global `IChatClient`, redaction on input and on prompt insertion, empty distillate stored to cache the hash); **Curation** (Memory-page card; manual edit stops auto-refresh; staleness hint; re-distill resets and rebuilds on next review); **First review of a project** (profile is used but stored only from the second review on — the project row is created by the audit sink); **Degradation** (no checkout ⇒ stored profile; failures fail open); **Configuration** (the three catalog keys + env-only `Sources`); **API** (the three routes). Verify every claim against the implemented code before writing it.

- [ ] **Step 2: Update `CLAUDE.md`.** In the extension-points section add a bullet after the review-memory entry:

```markdown
- **Architecture profile (distilled guidelines):** `IReviewGuidelines` (Core `Abstractions`)
  supplies a per-project architecture profile for the prompt; the default
  `DistillingReviewGuidelines` (`src/Naudit.Infrastructure/Guidelines/`) distills it from the
  repo's own docs (CLAUDE.md/AGENTS.md/README/CONTRIBUTING/docs/**.md) found in the shared
  checkout, hash-caches it in `ProjectGuidelinesEntity` (one blob per project, zero LLM calls
  while docs are unchanged), and respects human curation (`ManuallyEdited` blocks auto-refresh;
  WebUI card on the Memory page: view/edit/re-distill). Distillation uses the **global**
  `IChatClient` (never the author-session router). Rendered by `PromptBuilder` as an
  authoritative "Project guidelines" section before the memory section; the system prompt now
  also instructs architecture-altitude (file-less findings) and a security checklist.
  `Naudit:Review:Guidelines:Enabled=false` swaps in `NullReviewGuidelines`. Fail-open like
  memory/SAST. See `docs/review-guidelines.md`.
```

Also extend the request-flow paragraph's grounding sentence to mention the guidelines profile joins the prompt alongside memory.

- [ ] **Step 3: Full-suite gate**

Run: `dotnet test Naudit.slnx` — expected all green. Then `cd src/frontend && npm run lint && npm run build` — clean. (A rare pre-existing `GitWorkspaceProviderTests` /tmp-race flake is known; if exactly that one fails, re-run to confirm it's non-reproducing — any other failure is real.)

- [ ] **Step 4: Commit**

```bash
git add docs/review-guidelines.md CLAUDE.md
git commit -m "docs(guidelines): Architektur-Profil dokumentiert (Destillat, Kuration, Degradation, Config)"
```

---

## Self-Review

**1. Spec coverage** (`2026-07-18-architecture-profile-design.md`):
- Core seam + options (§Core additions) → Task 3. ✅
- Entity incl. `SourcesChangedAt`, unique per project (§Infrastructure) → Task 2. ✅
- Distiller steps 1–7 (stored-profile fallback, hash cache, curation-wins + stale signal, global client, caps, fail-open, empty-distillate hash caching) → Task 3. ✅ Deviation, deliberate: the spec's "upsert the row" presumes an existing project; on a project's **very first review** the row is created by the audit sink *after* the review (with ownership), so the distiller returns the profile without storing (Task 3 test `UnknownProject_…`) — documented in Task 7. This costs one repeat distillation on review 2 and avoids duplicating the sink's ownership logic.
- ReviewService flow + redaction + first-review cost (§Request-flow) → Task 4. ✅
- Prompt section placement + system-prompt altitude/security additions, byte-identical when empty (§Prompt changes) → Task 1. ✅
- WebUI card (view/edit/re-distill/staleness), API routes + auth (§WebUI) → Tasks 5+6. ✅
- Config keys + SettingsCatalog, `Sources` env-only (§Core additions/§Infrastructure) → Tasks 3+4. ✅
- Error handling/redaction-twice/injection note (§Error handling) → Tasks 3 (input redaction) + 4 (prompt-insertion redaction) + 7 (docs). ✅
- Testing incl. litmus test (§Testing) → Tasks 1–5 (litmus twice: distiller prompt in T3, end-to-end prompt in T4). ✅

**2. Placeholder scan:** all code steps carry complete code; the two "model on existing file X" steps (T4 wiring test, T5 endpoint tests) name the exact in-repo template file and enumerate the concrete test cases — no TBDs.

**3. Type consistency:** `IReviewGuidelines.GetAsync(string, string?, CancellationToken)` identical in T3/T4; `ReviewGuidelinesOptions` fields match T4's catalog keys and T5's cap usage; `ProjectGuidelinesEntity` fields match T2 entity/migration/T3 distiller/T5 endpoints/T6 DTO; `PromptBuilder.Build` trailing `guidelines` matches T1/T4; section heading string identical in T1 renderer and tests.
