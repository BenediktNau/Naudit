# Review Memory PR 1 (memory core + WebUI) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Per-project review memory (false positives + conventions) injected into the review prompt as maintainer guidance, curated via the WebUI — PR 1 of the review-memory track (spec `docs/superpowers/specs/2026-07-09-review-memory-design.md`).

**Architecture:** Same seam pattern as `IPromptRedactor`: interface + models in `Naudit.Core`, EF implementation in `Naudit.Infrastructure`, selection in `AddNauditInfrastructure`. New `MemoryEntryEntity` (one provider-neutral migration), deterministic `DbReviewMemory` selector (fail-open), prompt section rendered last by `PromptBuilder`, JSON API + React page following the existing BFF/WebUI patterns.

**Tech Stack:** .NET 10 Minimal API, EF Core (SQLite/Postgres), MEAI abstractions, xUnit, React 19 + TanStack Query + Tailwind 4.

## Global Constraints

- Build/test **only** via `Naudit.slnx` (`dotnet build Naudit.slnx`, `dotnet test Naudit.slnx`) — `Naudit.sln` does not exist.
- **Core rule:** `Naudit.Core` references only `Microsoft.Extensions.AI.Abstractions`. `IReviewMemory` + `MemoryEntry` live in Core; everything EF/HTTP lives in Infrastructure.
- Code comments in **German**; docs (`docs/**`, README) in **English**. UI copy in English (matches existing SPA).
- Migrations are **hand-kept provider-neutral**: no explicit column types in `Up()`, PK int columns annotated with BOTH `Sqlite:Autoincrement` and `Npgsql:ValueGenerationStrategy`, no `HasColumnType` in the new Designer (snapshot stays SQLite-baked — that is fine).
- TDD: write the failing test first, watch it fail, implement, watch it pass. One commit per task.
- Branch: `feat/review-memory` (already fast-forwarded to `origin/main`).
- Frontend checks: `cd src/frontend && npm run lint && npm run build` (assumes `npm ci` was run once).
- Full-suite runs may show environmental inotify flakes; single-class filters are authoritative for red/green steps, the full suite must pass in Task 9.

---

### Task 1: Core models + PromptBuilder memory section

**Files:**
- Create: `src/Naudit.Core/Models/MemoryEntry.cs`
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs` (filename typo is known/kept)
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Produces: `enum MemoryKind { FalsePositive, Convention }`; `record MemoryEntry(MemoryKind Kind, string? File, string Text, string? Reason)`; `PromptBuilder.Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes, IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null, IReadOnlyList<MemoryEntry>? memory = null, bool toolsAvailable = false)`.

- [ ] **Step 1: Write the failing tests** — append to `PromtBuilderTests.cs` (match the file's existing usings/helpers; the user message is `messages[1]`):

```csharp
[Fact]
public void Build_withMemory_rendersFalsePositivesAndConventions()
{
    var request = new ReviewRequest("1", 1, "T");
    var changes = new List<CodeChange> { new("a.cs", "@@ -0,0 +1,1 @@\n+x") };
    var memory = new List<MemoryEntry>
    {
        new(MemoryKind.FalsePositive, "src/Foo/Bar.cs", "Angeblich ungeschlossenes <li>", "Redactor-Artefakt"),
        new(MemoryKind.FalsePositive, null, "Tailwind-4-Syntax ist kein Fehler", null),
        new(MemoryKind.Convention, null, "Wir nutzen bewusst deutsche Code-Kommentare", null),
    };

    var messages = PromptBuilder.Build("SYS", request, changes, memory: memory);
    var user = messages[1].Text;

    Assert.Contains("# Project memory (maintainer guidance)", user);
    Assert.Contains("## Known false positives — do NOT report these or equivalent findings again", user);
    Assert.Contains("- src/Foo/Bar.cs: Angeblich ungeschlossenes <li> (maintainer note: Redactor-Artefakt)", user);
    Assert.Contains("- Tailwind-4-Syntax ist kein Fehler", user);
    Assert.Contains("## Project conventions — respect these when judging the diff", user);
    Assert.Contains("- Wir nutzen bewusst deutsche Code-Kommentare", user);
    // Gedächtnis ist die LETZTE Sektion (näher an der Antwort = höheres Gewicht).
    Assert.True(user.IndexOf("# Project memory", StringComparison.Ordinal)
        > user.IndexOf("# Static-analysis", StringComparison.Ordinal));
}

[Fact]
public void Build_withEmptyMemory_isByteIdenticalToNoMemory()
{
    var request = new ReviewRequest("1", 1, "T");
    var changes = new List<CodeChange> { new("a.cs", "@@ -0,0 +1,1 @@\n+x") };

    var without = PromptBuilder.Build("SYS", request, changes)[1].Text;
    var withEmpty = PromptBuilder.Build("SYS", request, changes, memory: [])[1].Text;

    Assert.Equal(without, withEmpty);
}

[Fact]
public void DefaultSystemPrompt_mentionsProjectMemory()
    => Assert.Contains("Project memory", PromptBuilder.DefaultSystemPrompt);
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: FAIL — compile error (`MemoryKind` not defined).

- [ ] **Step 3: Implement** — create `src/Naudit.Core/Models/MemoryEntry.cs`:

```csharp
namespace Naudit.Core.Models;

public enum MemoryKind { FalsePositive, Convention }

/// <summary>Ein Gedächtnis-Eintrag: als False Positive markierter Fund oder Projekt-Konvention.
/// File ist bei Konventionen reine Anzeige-Einordnung, nie Auswahlfilter.</summary>
public sealed record MemoryEntry(MemoryKind Kind, string? File, string Text, string? Reason);
```

In `PromtBuilder.cs`: extend the `Build` signature (insert `memory` BEFORE `toolsAvailable` — `ReviewService` passes `toolsAvailable` by name):

```csharp
public static IList<ChatMessage> Build(
    string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
    IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null,
    IReadOnlyList<MemoryEntry>? memory = null, bool toolsAvailable = false)
```

After the `AppendToolGuidance(sb, toolsAvailable);` call add `AppendMemory(sb, memory);` and add:

```csharp
// Maintainer-Guidance als LETZTE Sektion (am nächsten an der Antwort = höchstes Gewicht).
// Leeres Gedächtnis rendert nichts — der Prompt bleibt byte-identisch zu heute.
private static void AppendMemory(StringBuilder sb, IReadOnlyList<MemoryEntry>? memory)
{
    if (memory is null || memory.Count == 0)
        return;

    sb.AppendLine();
    sb.AppendLine("# Project memory (maintainer guidance)");

    AppendMemoryGroup(sb, "## Known false positives — do NOT report these or equivalent findings again",
        memory.Where(m => m.Kind == MemoryKind.FalsePositive));
    AppendMemoryGroup(sb, "## Project conventions — respect these when judging the diff",
        memory.Where(m => m.Kind == MemoryKind.Convention));
}

private static void AppendMemoryGroup(StringBuilder sb, string heading, IEnumerable<MemoryEntry> entries)
{
    var list = entries.ToList();
    if (list.Count == 0)
        return;
    sb.AppendLine();
    sb.AppendLine(heading);
    foreach (var m in list)
    {
        var scope = string.IsNullOrEmpty(m.File) ? "" : $"{m.File}: ";
        var note = string.IsNullOrEmpty(m.Reason) ? "" : $" (maintainer note: {m.Reason})";
        sb.AppendLine($"- {scope}{m.Text}{note}");
    }
}
```

Append one segment to the `DefaultSystemPrompt` string concatenation (after the "Repository context" sentence, keeping the single-string style):

```csharp
"A read-only \"Project memory\" section may follow — it contains maintainer decisions: " +
"do NOT report findings matching a known false positive again, and treat the listed conventions " +
"as authoritative project rules, not as issues to flag."
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromtBuilderTests`
Expected: PASS (all, including pre-existing).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Models/MemoryEntry.cs src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(memory): Prompt-Sektion Project memory (FPs + Konventionen) im PromptBuilder"
```

---

### Task 2: `IReviewMemory` seam + `ReviewMemoryOptions` + `ReviewService` wiring

**Files:**
- Create: `src/Naudit.Core/Abstractions/IReviewMemory.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeReviewMemory.cs`
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `MemoryEntry`/`MemoryKind` (Task 1).
- Produces: `IReviewMemory.SelectAsync(string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)` → `Task<IReadOnlyList<MemoryEntry>>`; `ReviewOptions.Memory` (`ReviewMemoryOptions { bool Enabled = true; int MaxEntries = 50 }`); `ReviewService` ctor gains trailing param `IReviewMemory reviewMemory`.

- [ ] **Step 1: Write the failing tests** — append to `ReviewServiceTests.cs`:

```csharp
[Fact]
public async Task ReviewAsync_passesMemoryEntries_intoPrompt()
{
    var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
    var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
    var memory = new FakeReviewMemory(new MemoryEntry(MemoryKind.Convention, null, "Deutsche Kommentare sind gewollt", null));
    var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, memory: memory);

    await service.ReviewAsync(Request);

    Assert.Equal("1", memory.LastProjectId);                     // ProjectId des Requests
    var user = chat.LastMessages![1].Text;
    Assert.Contains("# Project memory (maintainer guidance)", user);
    Assert.Contains("Deutsche Kommentare sind gewollt", user);
}

[Fact]
public async Task ReviewAsync_redactsMemoryEntries_beforePrompt()
{
    var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
    var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
    var memory = new FakeReviewMemory(
        new MemoryEntry(MemoryKind.FalsePositive, "a.cs", "enthält TOPSECRET", "auch TOPSECRET hier"));
    var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
        redactor: new MarkerRedactor(), memory: memory);

    await service.ReviewAsync(Request);

    var user = chat.LastMessages![1].Text;
    Assert.DoesNotContain("TOPSECRET", user);
    Assert.Contains("[MASKED]", user);
}

// Test-Redactor: ersetzt ein Markerwort — genug, um "Memory läuft durch den Redactor" zu beweisen.
private sealed class MarkerRedactor : IPromptRedactor
{
    public Task<string> RedactAsync(string text, CancellationToken ct = default)
        => Task.FromResult(text.Replace("TOPSECRET", "[MASKED]"));
}

[Fact]
public async Task ReviewAsync_withEmptyMemory_promptUnchanged()
{
    var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
    var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
    var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

    await service.ReviewAsync(Request);

    Assert.DoesNotContain("Project memory (maintainer guidance)", chat.LastMessages![1].Text);
}
```

Create `tests/Naudit.Tests/Fakes/FakeReviewMemory.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

/// <summary>Liefert fixe Einträge und protokolliert den Aufruf.</summary>
internal sealed class FakeReviewMemory(params MemoryEntry[] entries) : IReviewMemory
{
    public string? LastProjectId { get; private set; }

    public Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        LastProjectId = projectId;
        return Task.FromResult<IReadOnlyList<MemoryEntry>>(entries);
    }
}
```

Extend the `CreateService` helper in `ReviewServiceTests.cs` with a trailing optional parameter and pass-through:

```csharp
IReviewMemory? memory = null)
// ... im new(...):
memory ?? new FakeReviewMemory());
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: FAIL — compile error (`IReviewMemory` not defined).

- [ ] **Step 3: Implement** — create `src/Naudit.Core/Abstractions/IReviewMemory.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Liefert die für dieses Review relevanten Gedächtnis-Einträge (FPs + Konventionen).</summary>
public interface IReviewMemory
{
    // Bekommt bewusst die CodeChanges (nicht nur Pfade), damit ein späterer
    // Embedding-Selector ("RAG light") dieselbe Signatur nutzen kann.
    Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

In `ReviewOptions.cs` add to `ReviewOptions`:

```csharp
    /// <summary>Projekt-Gedächtnis: FPs + Konventionen als Prompt-Guidance (Naudit:Review:Memory).</summary>
    public ReviewMemoryOptions Memory { get; set; } = new();
```

and the options class at file level:

```csharp
/// <summary>Projekt-Gedächtnis. Default AN; Enabled=false ⇒ No-Op-Selector (heutiges Verhalten).</summary>
public sealed class ReviewMemoryOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Deckel für die Prompt-Sektion — Konventionen zuerst, dann FPs, je neueste zuerst.</summary>
    public int MaxEntries { get; set; } = 50;
}
```

In `ReviewService.cs`: add trailing ctor param `IReviewMemory reviewMemory`. After `GatherGroundingAsync` (before the redaction block) add:

```csharp
        // Projekt-Gedächtnis: Auswahl braucht kein Repo (unabhängig vom Checkout);
        // Fail-open lebt in der Implementierung — hier kommt schlimmstenfalls eine leere Liste an.
        var memory = await reviewMemory.SelectAsync(request.ProjectId, changes, ct);
```

In the redaction block (after `redContext`) add:

```csharp
        var redMemory = new List<MemoryEntry>(memory.Count);
        foreach (var m in memory)
            redMemory.Add(m with
            {
                Text = await redactor.RedactAsync(m.Text, ct),
                Reason = m.Reason is null ? null : await redactor.RedactAsync(m.Reason, ct),
            });
```

Change the `PromptBuilder.Build` call to pass `redMemory`:

```csharp
        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext,
            redMemory, toolsAvailable: tools.Count > 0);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: PASS. Then `dotnet build Naudit.slnx` — stays green: `AddScoped<ReviewService>()` resolves the new ctor dependency lazily at runtime, and the DI registration follows in Task 4. Should any other `new ReviewService(` call site fail to compile, append a `new FakeReviewMemory()` there.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Abstractions/IReviewMemory.cs src/Naudit.Core/Review/ReviewOptions.cs src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/Fakes/FakeReviewMemory.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(memory): IReviewMemory-Seam + ReviewService-Wiring (Auswahl, Redaction, Prompt)"
```

---

### Task 3: `MemoryEntryEntity` + DbContext + provider-neutral migration

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs`
- Modify: `src/Naudit.Infrastructure/Data/NauditDbContext.cs`
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260717120000_AddMemoryEntries.cs` (+ `.Designer.cs`, + snapshot update)
- Test: `tests/Naudit.Tests/DbReviewMemoryTests.cs` (new file, roundtrip test only — selection tests follow in Task 4)

**Interfaces:**
- Produces: `MemoryEntryEntity { int Id; int ProjectId; ProjectEntity Project; string Kind ("FalsePositive"|"Convention"); string? File; string Text; string? Reason; int? SourceFindingId; string CreatedBy; DateTime CreatedAt; bool Active }`; `NauditDbContext.MemoryEntries`.

- [ ] **Step 1: Write the failing test** — create `tests/Naudit.Tests/DbReviewMemoryTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DbReviewMemoryTests`
Expected: FAIL — compile error (`MemoryEntryEntity` not defined).

- [ ] **Step 3: Implement entity + DbContext** — append to `Entities.cs`:

```csharp
/// <summary>Projekt-Gedächtnis: als False Positive markierter Fund oder Projekt-Konvention.
/// Deaktivieren statt löschen — der Audit-Trail (wer, wann, warum) bleibt erhalten.</summary>
public sealed class MemoryEntryEntity
{
    public int Id { get; set; }
    public int ProjectId { get; set; }
    public ProjectEntity Project { get; set; } = null!;
    public required string Kind { get; set; }          // "FalsePositive" | "Convention" (String wie Severity/Verdict)
    public string? File { get; set; }                  // null: Konvention oder datei-loser FP
    public required string Text { get; set; }
    public string? Reason { get; set; }
    public int? SourceFindingId { get; set; }          // Idempotenz-Anker (unique unter nicht-null)
    public required string CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool Active { get; set; }
}
```

In `NauditDbContext.cs` add the DbSet:

```csharp
    public DbSet<MemoryEntryEntity> MemoryEntries => Set<MemoryEntryEntity>();
```

and in `OnModelCreating` (after the `ReviewFindingEntity` block):

```csharp
        b.Entity<MemoryEntryEntity>(e =>
        {
            e.HasIndex(x => new { x.ProjectId, x.Active });        // Selektion pro Projekt
            // NULLs kollidieren nicht (SQLite wie Postgres) — Konventionen haben kein SourceFinding.
            e.HasIndex(x => x.SourceFindingId).IsUnique();
            e.HasOne(x => x.Project).WithMany()
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Finding gelöscht (Review-Kaskade) ⇒ Eintrag bleibt, nur der Anker wird null.
            e.HasOne<ReviewFindingEntity>().WithMany()
                .HasForeignKey(x => x.SourceFindingId).OnDelete(DeleteBehavior.SetNull);
        });
```

- [ ] **Step 4: Add the migration** — generate then neutralize:

```bash
dotnet ef migrations add AddMemoryEntries --project src/Naudit.Infrastructure
```

(If `dotnet ef` is missing: `dotnet tool install --global dotnet-ef`.) Then hand-edit the generated `*_AddMemoryEntries.cs` so `Up()` is provider-neutral — replace its body with:

```csharp
            migrationBuilder.CreateTable(
                name: "MemoryEntries",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProjectId = table.Column<int>(nullable: false),
                    Kind = table.Column<string>(nullable: false),
                    File = table.Column<string>(nullable: true),
                    Text = table.Column<string>(nullable: false),
                    Reason = table.Column<string>(nullable: true),
                    SourceFindingId = table.Column<int>(nullable: true),
                    CreatedBy = table.Column<string>(nullable: false),
                    CreatedAt = table.Column<DateTime>(nullable: false),
                    Active = table.Column<bool>(nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemoryEntries", x => x.Id);
                    table.ForeignKey("FK_MemoryEntries_Projects_ProjectId", x => x.ProjectId,
                        principalTable: "Projects", principalColumn: "Id", onDelete: ReferentialAction.Cascade);
                    table.ForeignKey("FK_MemoryEntries_ReviewFindings_SourceFindingId", x => x.SourceFindingId,
                        principalTable: "ReviewFindings", principalColumn: "Id", onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex("IX_MemoryEntries_ProjectId_Active", "MemoryEntries", new[] { "ProjectId", "Active" });
            migrationBuilder.CreateIndex("IX_MemoryEntries_SourceFindingId", "MemoryEntries", "SourceFindingId", unique: true);
```

`Down()` = `migrationBuilder.DropTable(name: "MemoryEntries");`. Add `using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;` and the file-level comment `// Wie AddSharePoolFlag bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).` Mirror the existing migrations. In the new `.Designer.cs`, delete every `.HasColumnType(...)` call **on the MemoryEntries builder only** (the rest is baked snapshot — leave it; the model snapshot also stays SQLite-baked, per repo convention).

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DbReviewMemoryTests`
Expected: PASS. Also run `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter NauditDbContextTests` — the existing migration tests must stay green.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Data/ tests/Naudit.Tests/DbReviewMemoryTests.cs
git commit -m "feat(memory): MemoryEntryEntity + provider-neutrale Migration AddMemoryEntries"
```

---

### Task 4: `DbReviewMemory` + `NullReviewMemory` + DI + `SettingsCatalog`

**Files:**
- Create: `src/Naudit.Infrastructure/Memory/DbReviewMemory.cs`
- Create: `src/Naudit.Infrastructure/Memory/NullReviewMemory.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Test: `tests/Naudit.Tests/DbReviewMemoryTests.cs`, Create: `tests/Naudit.Tests/ReviewMemoryWiringTests.cs`

**Interfaces:**
- Consumes: `IReviewMemory`, `MemoryEntry`, `ReviewOptions.Memory.MaxEntries` (Task 2), `MemoryEntryEntity` (Task 3).
- Produces: `DbReviewMemory(NauditDbContext db, ReviewOptions options, ILogger<DbReviewMemory> logger)`; `NullReviewMemory` (empty list); DI: `Naudit:Review:Memory:Enabled=false` ⇒ `NullReviewMemory`, sonst `DbReviewMemory` (scoped).

- [ ] **Step 1: Write the failing tests** — append to `DbReviewMemoryTests.cs` (uses `TestDb` from `Fakes` for speed; add `using Microsoft.Extensions.Logging.Abstractions; using Naudit.Core.Models; using Naudit.Core.Review; using Naudit.Infrastructure.Memory; using Naudit.Tests.Fakes;`):

```csharp
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
```

Create `tests/Naudit.Tests/ReviewMemoryWiringTests.cs` (mirrors `AiClientRouterWiringTests`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Memory;
using Xunit;

namespace Naudit.Tests;

public class ReviewMemoryWiringTests
{
    private static void AssertMemoryType(Dictionary<string, string?> settings, Type expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IReviewMemory>());
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Default_registersDbReviewMemory()
        => AssertMemoryType(BaseSettings(), typeof(DbReviewMemory));

    [Fact]
    public void Disabled_registersNullReviewMemory()
    {
        var settings = BaseSettings();
        settings["Naudit:Review:Memory:Enabled"] = "false";
        AssertMemoryType(settings, typeof(NullReviewMemory));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DbReviewMemoryTests|ReviewMemoryWiringTests"`
Expected: FAIL — compile error (`DbReviewMemory` not defined).

- [ ] **Step 3: Implement** — create `src/Naudit.Infrastructure/Memory/DbReviewMemory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Memory;

/// <summary>Deterministischer Default-Selector: alle Konventionen (File ist Anzeige, nie Filter)
/// + FPs mit exaktem Datei-Match im Diff + datei-lose FPs; Deckel MaxEntries — Konventionen
/// zuerst, dann FPs, je neueste zuerst. Fail-open: jeder Fehler ⇒ leere Liste, geloggt —
/// ein Gedächtnis-Schluckauf kippt nie das Review (Audit-Sink-Philosophie).</summary>
public sealed class DbReviewMemory(NauditDbContext db, ReviewOptions options, ILogger<DbReviewMemory> logger)
    : IReviewMemory
{
    public async Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        try
        {
            var project = await db.Projects.SingleOrDefaultAsync(p => p.PlatformProjectId == projectId, ct);
            if (project is null)
                return [];

            var entries = await db.MemoryEntries
                .Where(m => m.ProjectId == project.Id && m.Active)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .ToListAsync(ct);

            var files = new HashSet<string>(changes.Select(c => c.FilePath));
            var conventions = entries.Where(m => m.Kind == "Convention");
            var fps = entries.Where(m => m.Kind == "FalsePositive" && (m.File is null || files.Contains(m.File)));

            return conventions.Concat(fps)
                .Take(Math.Max(0, options.Memory.MaxEntries))
                .Select(m => new MemoryEntry(
                    m.Kind == "Convention" ? MemoryKind.Convention : MemoryKind.FalsePositive,
                    m.File, m.Text, m.Reason))
                .ToList();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Gedächtnis-Auswahl fehlgeschlagen — Review läuft ohne Memory weiter.");
            return [];
        }
    }
}
```

Create `src/Naudit.Infrastructure/Memory/NullReviewMemory.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Memory;

/// <summary>No-Op bei Naudit:Review:Memory:Enabled=false — heutiges Verhalten.</summary>
public sealed class NullReviewMemory : IReviewMemory
{
    public Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
}
```

In `DependencyInjection.cs` (near the redactor registration; `reviewOptions` is already in scope; add `using Naudit.Infrastructure.Memory;`):

```csharp
        // Projekt-Gedächtnis: FPs + Konventionen als Prompt-Guidance. Default AN;
        // aus ⇒ NullReviewMemory (leere Liste) = exakt heutiges Verhalten.
        if (reviewOptions.Memory.Enabled)
            services.AddScoped<IReviewMemory, DbReviewMemory>();
        else
            services.AddSingleton<IReviewMemory, NullReviewMemory>();
```

In `SettingsCatalog.cs` add after `new("Naudit:Review:MaxRoundtrips", false),`:

```csharp
        new("Naudit:Review:Memory:Enabled", false),
        new("Naudit:Review:Memory:MaxEntries", false),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DbReviewMemoryTests|ReviewMemoryWiringTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Memory/ src/Naudit.Infrastructure/DependencyInjection.cs src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/DbReviewMemoryTests.cs tests/Naudit.Tests/ReviewMemoryWiringTests.cs
git commit -m "feat(memory): DbReviewMemory (deterministisch, fail-open) + DI + Settings-Keys"
```

---

### Task 5: API — finding ids in review detail + FP mark/undo endpoints

**Files:**
- Modify: `src/Naudit.Web/Endpoints/CurrentAccount.cs` (move visibility helper here)
- Modify: `src/Naudit.Web/Endpoints/DataEndpoints.cs`
- Create: `src/Naudit.Web/Endpoints/MemoryEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (map next to `app.MapDataEndpoints();`)
- Test: `tests/Naudit.Tests/MemoryEndpointTests.cs`

**Interfaces:**
- Consumes: `MemoryEntryEntity` (Task 3), `CurrentAccount.GetActiveAsync` (existing).
- Produces: `CurrentAccount.VisibleProjects(NauditDbContext, AccountEntity)` (moved from `DataEndpoints`, now public) and `CurrentAccount.CanSeeProjectAsync(NauditDbContext, AccountEntity, int projectId, CancellationToken)`; `POST /api/findings/{id}/false-positive` body `{ "reason": string? }` → `200 { id, active }` / 404 / 403; `DELETE` same route → 204 (idempotent); `/api/reviews/{id}` findings now carry `id` and `falsePositive`.

- [ ] **Step 1: Write the failing tests** — create `tests/Naudit.Tests/MemoryEndpointTests.cs` (login/seed idiom copied from `DataEndpointTests`):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class MemoryEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public MemoryEndpointTests(TestAppFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, WebApplicationFactory<Program> Factory)> AdminApp()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-memapi-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:WebhookSecret", "s");
            b.UseSetting("Naudit:Ai:Provider", "Ollama");
            b.UseSetting("Naudit:Ai:Model", "llama3.1");
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
        });
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return (client, factory);
    }

    /// <summary>Projekt + Review + 1 Finding seeden; liefert Ids für die Endpoint-Aufrufe.</summary>
    private static async Task<(int ProjectId, int ReviewId, int FindingId)> Seed(
        WebApplicationFactory<Program> factory, string project = "owner/repo")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        var p = await db.Projects.SingleOrDefaultAsync(x => x.PlatformProjectId == project)
            ?? db.Projects.Add(new ProjectEntity { PlatformProjectId = project, FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow }).Entity;
        var f = new ReviewFindingEntity { Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "Fund" };
        var r = new ReviewEntity
        {
            Project = p, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
            CreatedAt = DateTime.UtcNow, Findings = { f },
        };
        db.Reviews.Add(r);
        await db.SaveChangesAsync();
        return (p.Id, r.Id, f.Id);
    }

    [Fact]
    public async Task MarkFalsePositive_createsEntry_isIdempotent_andFlagsReviewDetail()
    {
        var (client, factory) = await AdminApp();
        var (projectId, reviewId, findingId) = await Seed(factory);

        var first = await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = "kein Bug" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var second = await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { reason = "immer noch keiner" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
            var entry = await db.MemoryEntries.SingleAsync();          // idempotent: genau EIN Eintrag
            Assert.Equal("FalsePositive", entry.Kind);
            Assert.Equal(projectId, entry.ProjectId);
            Assert.Equal(findingId, entry.SourceFindingId);
            Assert.Equal("immer noch keiner", entry.Reason);           // Reason aktualisiert
            Assert.Equal("root", entry.CreatedBy);
            Assert.True(entry.Active);
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{reviewId}");
        var finding = detail.GetProperty("findings")[0];
        Assert.Equal(findingId, finding.GetProperty("id").GetInt32());
        Assert.True(finding.GetProperty("falsePositive").GetBoolean());
    }

    [Fact]
    public async Task UnmarkFalsePositive_deactivates_andIsIdempotent()
    {
        var (client, factory) = await AdminApp();
        var (_, reviewId, findingId) = await Seed(factory);
        await client.PostAsJsonAsync($"/api/findings/{findingId}/false-positive", new { });

        var undo = await client.DeleteAsync($"/api/findings/{findingId}/false-positive");
        Assert.Equal(HttpStatusCode.NoContent, undo.StatusCode);
        var again = await client.DeleteAsync($"/api/findings/{findingId}/false-positive");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);      // kein Eintrag ⇒ trotzdem 204

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        Assert.False((await db.MemoryEntries.SingleAsync()).Active);   // deaktiviert, nicht gelöscht

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/reviews/{reviewId}");
        Assert.False(detail.GetProperty("findings")[0].GetProperty("falsePositive").GetBoolean());
    }

    [Fact]
    public async Task MarkFalsePositive_unknownFinding_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.PostAsJsonAsync("/api/findings/99999/false-positive", new { });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryApi_unauthenticated_returns401()
    {
        var (_, factory) = await AdminApp();
        var anon = factory.CreateDefaultClient();                      // kein Login-Cookie
        var resp = await anon.PostAsJsonAsync("/api/findings/1/false-positive", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter MemoryEndpointTests`
Expected: FAIL — 404 on the routes (`MarkFalsePositive_*`) and missing `id`/`falsePositive` properties.

- [ ] **Step 3: Implement.** In `CurrentAccount.cs` add (moving the logic out of `DataEndpoints`; add `using Microsoft.EntityFrameworkCore;` if missing):

```csharp
    /// <summary>Admin: alle Projekte. Sonst: Projekte, deren Owner-Anteil in den eigenen Links liegt.
    /// (Aus DataEndpoints hierher gezogen — jetzt von Data- UND Memory-API genutzt.)</summary>
    public static IQueryable<ProjectEntity> VisibleProjects(NauditDbContext db, AccountEntity acct)
    {
        if (acct.IsAdmin) return db.Projects;
        var logins = db.GitHubLinks.Where(l => l.AccountId == acct.Id).Select(l => l.Login);
        // Owner = Teil vor '/'; GitLab-Ids matchen als Ganzes (Links sind lowercased gespeichert).
        return db.Projects.Where(p =>
            logins.Any(l => p.PlatformProjectId.ToLower() == l || EF.Functions.Like(p.PlatformProjectId.ToLower(), l + "/%")));
    }

    public static Task<bool> CanSeeProjectAsync(NauditDbContext db, AccountEntity acct, int projectId, CancellationToken ct)
        => VisibleProjects(db, acct).AnyAsync(p => p.Id == projectId, ct);
```

In `DataEndpoints.cs`: delete the private `VisibleProjects` method and replace its three call sites with `CurrentAccount.VisibleProjects(db, acct)`. In the `/api/reviews/{id}` handler, before building the response add:

```csharp
            // Aktive FP-Markierungen zu diesen Findings — fürs UI-Toggle im Review-Detail.
            var findingIds = review.Findings.Select(f => f.Id).ToList();
            var fpIds = await db.MemoryEntries
                .Where(m => m.Active && m.SourceFindingId != null && findingIds.Contains(m.SourceFindingId.Value))
                .Select(m => m.SourceFindingId!.Value)
                .ToListAsync(ctx.RequestAborted);
```

and extend the findings projection:

```csharp
                findings = review.Findings.Select(f => new
                {
                    id = f.Id,
                    severity = f.Severity,
                    confidence = f.Confidence,
                    file = f.File,
                    line = f.Line,
                    text = f.Text,
                    falsePositive = fpIds.Contains(f.Id),
                }),
```

Create `src/Naudit.Web/Endpoints/MemoryEndpoints.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Projekt-Gedächtnis-API: FP-Markierung am Finding + Konventionen-Verwaltung.
/// Sichtbarkeit wie das Dashboard (eigene Projekte bzw. Admin), 401/403 statt Redirects.</summary>
public static class MemoryEndpoints
{
    private sealed record FpBody(string? Reason);

    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPost("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id, FpBody? body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            // Idempotent: der Eintrag zum selben Finding wird reaktiviert/aktualisiert, nie dupliziert.
            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == id, ctx.RequestAborted);
            if (entry is null)
            {
                entry = new MemoryEntryEntity
                {
                    ProjectId = finding.Review.ProjectId,
                    Kind = "FalsePositive",
                    File = finding.File,
                    Text = finding.Text,
                    SourceFindingId = id,
                    CreatedBy = acct.Username,
                    CreatedAt = DateTime.UtcNow,
                    Active = true,
                };
                db.MemoryEntries.Add(entry);
            }
            entry.Active = true;
            if (!string.IsNullOrWhiteSpace(body?.Reason))
                entry.Reason = body!.Reason!.Trim();
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });

        // Undo (Fehlklick): deaktivieren statt löschen — idempotent, kein Eintrag ⇒ trotzdem 204.
        api.MapDelete("/findings/{id:int}/false-positive", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.SourceFindingId == id, ctx.RequestAborted);
            if (entry is not null)
            {
                if (!await CurrentAccount.CanSeeProjectAsync(db, acct, entry.ProjectId, ctx.RequestAborted))
                    return Results.Forbid();
                entry.Active = false;
                await db.SaveChangesAsync(ctx.RequestAborted);
            }
            return Results.NoContent();
        });
    }
}
```

In `Program.cs`, directly after `app.MapDataEndpoints();` add:

```csharp
    app.MapMemoryEndpoints();
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "MemoryEndpointTests|DataEndpointTests"`
Expected: PASS (both — the moved helper must not break the data endpoints).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/ src/Naudit.Web/Program.cs tests/Naudit.Tests/MemoryEndpointTests.cs
git commit -m "feat(memory): FP-Markierung am Finding (API) + Finding-Ids im Review-Detail"
```

---

### Task 6: API — memory list / create convention / toggle

**Files:**
- Modify: `src/Naudit.Web/Endpoints/MemoryEndpoints.cs`
- Test: `tests/Naudit.Tests/MemoryEndpointTests.cs`

**Interfaces:**
- Produces: `GET /api/projects/{id}/memory` → `{ entries: [{ id, kind, file, text, reason, createdBy, createdAt, active, sourceFindingId }] }` (newest first, incl. inactive); `POST /api/projects/{id}/memory` body `{ text, file? }` → 200 entry / 400 empty text; `PUT /api/memory/{id}` body `{ active }` → 200 / 404.

- [ ] **Step 1: Write the failing tests** — append to `MemoryEndpointTests.cs`:

```csharp
    [Fact]
    public async Task Conventions_createListToggle_roundtrip()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory, "owner/conv-repo");

        var create = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory",
            new { text = "Wir nutzen bewusst Tailwind 4", file = (string?)null });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);
        var created = await create.Content.ReadFromJsonAsync<JsonElement>();
        var entryId = created.GetProperty("id").GetInt32();

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/memory");
        var entries = list.GetProperty("entries");
        Assert.Equal(1, entries.GetArrayLength());
        Assert.Equal("Convention", entries[0].GetProperty("kind").GetString());
        Assert.Equal("Wir nutzen bewusst Tailwind 4", entries[0].GetProperty("text").GetString());
        Assert.True(entries[0].GetProperty("active").GetBoolean());

        var toggle = await client.PutAsJsonAsync($"/api/memory/{entryId}", new { active = false });
        Assert.Equal(HttpStatusCode.OK, toggle.StatusCode);
        list = await client.GetFromJsonAsync<JsonElement>($"/api/projects/{projectId}/memory");
        Assert.False(list.GetProperty("entries")[0].GetProperty("active").GetBoolean()); // inaktiv bleibt gelistet
    }

    [Fact]
    public async Task CreateConvention_emptyText_returns400()
    {
        var (client, factory) = await AdminApp();
        var (projectId, _, _) = await Seed(factory, "owner/conv-repo2");
        var resp = await client.PostAsJsonAsync($"/api/projects/{projectId}/memory", new { text = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task MemoryList_unknownProject_returns404()
    {
        var (client, _) = await AdminApp();
        var resp = await client.GetAsync("/api/projects/99999/memory");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter MemoryEndpointTests`
Expected: FAIL — 404 on the new routes.

- [ ] **Step 3: Implement** — add to `MapMemoryEndpoints` (records next to `FpBody`):

```csharp
    private sealed record ConventionBody(string? Text, string? File);
    private sealed record ToggleBody(bool Active);
```

```csharp
        api.MapGet("/projects/{id:int}/memory", async (HttpContext ctx, NauditDbContext db, int id) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var entries = await db.MemoryEntries
                .Where(m => m.ProjectId == id)
                .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
                .Select(m => new
                {
                    id = m.Id, kind = m.Kind, file = m.File, text = m.Text, reason = m.Reason,
                    createdBy = m.CreatedBy, createdAt = m.CreatedAt, active = m.Active,
                    sourceFindingId = m.SourceFindingId,
                })
                .ToListAsync(ctx.RequestAborted);
            return Results.Ok(new { entries });
        });

        api.MapPost("/projects/{id:int}/memory", async (HttpContext ctx, NauditDbContext db, int id, ConventionBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(body.Text))
                return Results.BadRequest(new { error = "text must not be empty" });
            if (!await db.Projects.AnyAsync(p => p.Id == id, ctx.RequestAborted)) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, id, ctx.RequestAborted)) return Results.Forbid();

            var entry = new MemoryEntryEntity
            {
                ProjectId = id,
                Kind = "Convention",
                File = string.IsNullOrWhiteSpace(body.File) ? null : body.File.Trim(),
                Text = body.Text.Trim(),
                CreatedBy = acct.Username,
                CreatedAt = DateTime.UtcNow,
                Active = true,
            };
            db.MemoryEntries.Add(entry);
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });

        api.MapPut("/memory/{id:int}", async (HttpContext ctx, NauditDbContext db, int id, ToggleBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();

            var entry = await db.MemoryEntries.SingleOrDefaultAsync(m => m.Id == id, ctx.RequestAborted);
            if (entry is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, entry.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            entry.Active = body.Active;
            await db.SaveChangesAsync(ctx.RequestAborted);
            return Results.Ok(new { id = entry.Id, active = entry.Active });
        });
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter MemoryEndpointTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/MemoryEndpoints.cs tests/Naudit.Tests/MemoryEndpointTests.cs
git commit -m "feat(memory): Konventionen-API (Liste, Anlegen, Aktiv-Toggle)"
```

---

### Task 7: Frontend — FP action in the review detail

**Files:**
- Modify: `src/frontend/src/api/types.ts`
- Modify: `src/frontend/src/hooks/mutations.ts`
- Modify: `src/frontend/src/components/ReviewDetail.tsx`

**Interfaces:**
- Consumes: `POST/DELETE /api/findings/{id}/false-positive` (Task 5); `api<T>(path, init?)` from `@/api/client`.
- Produces: `ReviewDetailDto` finding fields `id: number`, `falsePositive: boolean`; hooks `useMarkFalsePositive(reviewId)` / `useUnmarkFalsePositive(reviewId)`.

- [ ] **Step 1: Extend the types** — in `types.ts`, extend the findings element type of `ReviewDetailDto` with:

```ts
  id: number;
  falsePositive: boolean;
```

- [ ] **Step 2: Add the mutations** — append to `hooks/mutations.ts`:

```ts
// Zusätzlich benötigter Import oben in der Datei (useMutation/useQueryClient sind dort schon importiert):
import { api } from "@/api/client";

/** FP-Markierung am Finding — invalidiert das Review-Detail (Flag kommt vom Server zurück). */
export function useMarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ findingId, reason }: { findingId: number; reason?: string }) =>
      api<{ id: number; active: boolean }>(`/api/findings/${findingId}/false-positive`, {
        method: "POST",
        body: JSON.stringify({ reason: reason ?? null }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}

export function useUnmarkFalsePositive(reviewId: number) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (findingId: number) =>
      api<void>(`/api/findings/${findingId}/false-positive`, { method: "DELETE" }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}
```

- [ ] **Step 3: Add the toggle to `ReviewDetail.tsx`** — inside the component get the hooks (`const mark = useMarkFalsePositive(id); const unmark = useUnmarkFalsePositive(id);`), give the finding row container `justify-between` via a wrapper, and append per finding (after the text `div`):

```tsx
              <button
                className={`ml-auto shrink-0 self-start rounded px-1.5 py-0.5 font-mono text-[10px] ${
                  f.falsePositive ? "bg-warn/12 text-warn" : "text-ink3 hover:text-warn"
                }`}
                title={
                  f.falsePositive
                    ? "Marked as false positive — click to undo"
                    : "Mark as false positive (feeds the project memory)"
                }
                onClick={() =>
                  f.falsePositive ? unmark.mutate(f.id) : mark.mutate({ findingId: f.id })
                }
              >
                {f.falsePositive ? "FP ✓" : "FP"}
              </button>
```

Use `f.id` as the row `key` instead of the array index while touching the map.

- [ ] **Step 4: Verify**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: both green (`build` runs `tsc --noEmit`).

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src/api/types.ts src/frontend/src/hooks/mutations.ts src/frontend/src/components/ReviewDetail.tsx
git commit -m "feat(memory): FP-Toggle am Finding im Review-Detail (WebUI)"
```

---

### Task 8: Frontend — memory page + navigation

**Files:**
- Create: `src/frontend/src/components/pages/MemoryPage.tsx`
- Modify: `src/frontend/src/hooks/queries.ts`, `src/frontend/src/hooks/mutations.ts`
- Modify: `src/frontend/src/api/types.ts`
- Modify: `src/frontend/src/App.tsx`, `src/frontend/src/components/TopBar.tsx`

**Interfaces:**
- Consumes: `GET /api/projects/{id}/memory`, `POST /api/projects/{id}/memory`, `PUT /api/memory/{id}` (Task 6); `useDashboard()` for the project list.
- Produces: `AppPage` union gains `"memory"`; hooks `useProjectMemory(projectId)`, `useCreateConvention(projectId)`, `useToggleMemoryEntry(projectId)`.

- [ ] **Step 1: Types + hooks.** In `types.ts` add:

```ts
export type MemoryEntryDto = {
  id: number;
  kind: "FalsePositive" | "Convention";
  file: string | null;
  text: string;
  reason: string | null;
  createdBy: string;
  createdAt: string;
  active: boolean;
  sourceFindingId: number | null;
};
export type ProjectMemoryDto = { entries: MemoryEntryDto[] };
```

In `queries.ts` add:

```ts
export function useProjectMemory(projectId: number | null) {
  return useQuery({
    queryKey: ["memory", projectId],
    queryFn: () => api<ProjectMemoryDto>(`/api/projects/${projectId}/memory`),
    enabled: projectId !== null,
  });
}
```

In `mutations.ts` add:

```ts
export function useCreateConvention(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { text: string; file?: string }) =>
      api<{ id: number }>(`/api/projects/${projectId}/memory`, {
        method: "POST",
        body: JSON.stringify({ text: vars.text, file: vars.file ?? null }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["memory", projectId] }),
  });
}

export function useToggleMemoryEntry(projectId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { id: number; active: boolean }) =>
      api<{ id: number; active: boolean }>(`/api/memory/${vars.id}`, {
        method: "PUT",
        body: JSON.stringify({ active: vars.active }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["memory", projectId] }),
  });
}
```

- [ ] **Step 2: Create `MemoryPage.tsx`:**

```tsx
import { useState } from "react";
import { useDashboard, useProjectMemory } from "@/hooks/queries";
import { useCreateConvention, useToggleMemoryEntry } from "@/hooks/mutations";
import { Skeleton } from "@/components/ui/Skeleton";

const kindPill: Record<string, string> = {
  FalsePositive: "text-warn bg-warn/12",
  Convention: "text-teal bg-teal/12",
};

/** Projekt-Gedächtnis: FP-Markierungen + Konventionen je Projekt einsehen und pflegen. */
export function MemoryPage() {
  const { data: dash, isLoading } = useDashboard();
  const [projectId, setProjectId] = useState<number | null>(null);
  const selected = projectId ?? dash?.projects[0]?.id ?? null;
  const { data: memory, isLoading: memLoading } = useProjectMemory(selected);
  const create = useCreateConvention(selected);
  const toggle = useToggleMemoryEntry(selected);
  const [text, setText] = useState("");
  const [file, setFile] = useState("");

  if (isLoading) return <div className="p-7"><Skeleton className="h-4 w-64" /></div>;
  if (!dash || dash.projects.length === 0)
    return <div className="p-7 font-mono text-[13px] text-ink3">No reviewed projects yet — memory entries attach to projects.</div>;

  const submit = () => {
    const t = text.trim();
    if (!t) return;
    create.mutate({ text: t, file: file.trim() || undefined }, { onSuccess: () => { setText(""); setFile(""); } });
  };

  return (
    <div className="flex flex-col gap-5 p-7">
      <div className="flex items-center gap-3">
        <h1 className="text-[15px] font-semibold text-ink">Project memory</h1>
        <select
          className="rounded-lg border border-border bg-elev px-2.5 py-1.5 font-mono text-[12.5px] text-ink"
          value={selected ?? undefined}
          onChange={(e) => setProjectId(Number(e.target.value))}
        >
          {dash.projects.map((p) => (
            <option key={p.id} value={p.id}>{p.name}</option>
          ))}
        </select>
      </div>

      {/* Konvention anlegen */}
      <div className="flex flex-wrap items-center gap-2">
        <input
          className="min-w-[32ch] flex-1 rounded-lg border border-border bg-elev px-2.5 py-1.5 text-[13px] text-ink placeholder:text-ink3"
          placeholder="New convention — e.g. “German code comments are intentional”"
          value={text}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
        />
        <input
          className="w-[24ch] rounded-lg border border-border bg-elev px-2.5 py-1.5 font-mono text-[12px] text-ink placeholder:text-ink3"
          placeholder="file scope (optional)"
          value={file}
          onChange={(e) => setFile(e.target.value)}
          onKeyDown={(e) => e.key === "Enter" && submit()}
        />
        <button
          className="rounded-lg bg-acc/12 px-3 py-1.5 text-[13px] font-semibold text-acc disabled:opacity-50"
          disabled={!text.trim() || create.isPending}
          onClick={submit}
        >
          Add
        </button>
      </div>

      {/* Einträge */}
      {memLoading && <Skeleton className="h-3 w-full max-w-[70ch]" />}
      {memory && memory.entries.length === 0 && (
        <div className="font-mono text-[12.5px] text-ink3">No memory entries yet. Mark a finding as false positive or add a convention.</div>
      )}
      {memory && memory.entries.length > 0 && (
        <div className="flex flex-col gap-2">
          {memory.entries.map((m) => (
            <div key={m.id} className={`flex items-start gap-2.5 ${m.active ? "" : "opacity-50"}`}>
              <span className={`mt-px shrink-0 rounded px-1.5 py-0.5 font-mono text-[10px] ${kindPill[m.kind]}`}>
                {m.kind === "FalsePositive" ? "FP" : "convention"}
              </span>
              <div className="min-w-0 flex-1 text-[12.5px] leading-snug text-ink2">
                {m.file && <span className="font-mono text-ink3">{m.file} — </span>}
                {m.text}
                {m.reason && <span className="text-ink3"> · {m.reason}</span>}
                <span className="ml-1.5 font-mono text-[10.5px] text-ink3">
                  {m.createdBy} · {new Date(m.createdAt).toLocaleDateString()}
                </span>
              </div>
              <button
                className="shrink-0 rounded px-1.5 py-0.5 font-mono text-[10px] text-ink3 hover:text-ink"
                title={m.active ? "Deactivate (kept for audit)" : "Reactivate"}
                onClick={() => toggle.mutate({ id: m.id, active: !m.active })}
              >
                {m.active ? "deactivate" : "activate"}
              </button>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
```

(If `DashboardDto.projects` elements lack `id`/`name` in `types.ts`, extend that type accordingly — the API already returns both.)

- [ ] **Step 3: Wire navigation.** In `App.tsx`: extend `AppPage` with `"memory"`, import `MemoryPage`, render `{page === "memory" && <MemoryPage />}`. In `TopBar.tsx` add after the Dashboard button (visible to every signed-in user, not admin-gated):

```tsx
        <button className={tab(page === "memory")} onClick={() => onNavigate("memory")}>
          Memory
        </button>
```

- [ ] **Step 4: Verify**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: green.

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src
git commit -m "feat(memory): Memory-Seite (Konventionen + FP-Einträge) + Navigation (WebUI)"
```

---

### Task 9: Docs + full verification

**Files:**
- Create: `docs/review-memory.md`
- Modify: `docs/configuration.md` (new keys), `CLAUDE.md` (extension-point bullet)

- [ ] **Step 1: Write `docs/review-memory.md`** (English, following the style of `docs/redaction.md`/`docs/review-context.md`): what the memory is (FPs + conventions, per project, human-curated), how entries affect the prompt (guidance section, no post-filtering, verdict stays gated), the two config keys (`Naudit:Review:Memory:Enabled` default `true`, `Naudit:Review:Memory:MaxEntries` default `50`), the WebUI flows (FP toggle in the review detail, memory page), the API routes, fail-open behavior, the prompt-injection note from the spec (accepted risk, authorization + attribution as mitigation), and an outlook box: PR 2 adds the `@naudit fp` reply command (store the GitLab note id alongside the discussion id — forward requirement from `docs/superpowers/specs/2026-07-17-review-analytics-design.md`).

- [ ] **Step 2: Update `docs/configuration.md`** — add the two keys to the review section table with defaults and one-line descriptions.

- [ ] **Step 3: Update `CLAUDE.md`** — add an extension-point bullet ("Review memory: `IReviewMemory` (Core) + `DbReviewMemory`/`NullReviewMemory` in `src/Naudit.Infrastructure/Memory/`, selected via `Naudit:Review:Memory:Enabled`; prompt section rendered last by `PromptBuilder`; WebUI FP toggle + memory page; see `docs/review-memory.md`").

- [ ] **Step 4: Full verification**

```bash
dotnet test Naudit.slnx
cd src/frontend && npm run lint && npm run build
```

Expected: full suite green (known environmental inotify flakes aside — rerun the affected class in isolation to confirm), frontend green.

- [ ] **Step 5: Commit**

```bash
git add docs/review-memory.md docs/configuration.md CLAUDE.md
git commit -m "docs(memory): review-memory.md + Konfig-Keys + CLAUDE.md-Erweiterungspunkt"
```
