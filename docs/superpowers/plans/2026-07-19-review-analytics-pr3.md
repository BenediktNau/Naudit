# Review Analytics PR 3 — Finding-Resolution-Tracking + Acceptance-Dashboard (explicit signals)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit records what happened to each finding (accepted / rejected) from explicit in-PR signals (`@naudit ok`, `@naudit fp`) and WebUI buttons, and surfaces acceptance-rate / FP-rate / severity-breakdown / weekly-trend / memory-impact on a new "Auswertung" dashboard.

**Architecture:** New nullable resolution columns on `ReviewFindingEntity` + memory-impact columns on `MemoryEntryEntity` (one provider-neutral migration; `PlatformNoteId` already exists from PR 2a). A shared `ResolutionWriter` enforces the precedence rule (explicit sources overwrite, undo clears only its own source). The existing `@naudit fp` reply-command plumbing (parser, `ReviewCommentReply`, `ReviewCommentCommandService`, `IReviewCommentResponder`) is generalized to also carry `@naudit ok`. WebUI gets a resolution action and an analytics endpoint + page. Everything gated by `Naudit:Review:Resolution:Enabled` (default true), fail-closed authorization, fail-open processing.

**Tech Stack:** .NET 10, EF Core (SQLite/Postgres, hand-neutralized migrations), ASP.NET Minimal API, React + TanStack Query + Tailwind 4, xUnit.

**Spec:** `docs/superpowers/specs/2026-07-17-review-analytics-design.md` — the authoritative requirements (already committed on this branch). Branch: `feat/review-analytics-pr3` (off main). This is **PR 3** (explicit signals + dashboard); PR 4 (LLM classification, GitHub checkbox, GitLab emoji) is a separate later plan.

## Global Constraints

- Solution file is `Naudit.slnx` — `dotnet build Naudit.slnx`; single class: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter <Name>`. Frontend: `cd src/frontend && npm ci && npm run lint && npm run build`.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. PR 3 adds **no Core types** — resolution logic lives in Infrastructure (`ResolutionWriter`, entity columns), endpoints in Web. The command-parser + reply DTO are already in `Naudit.Infrastructure.Git`.
- **No `dotnet-ef` in this environment** — migration, its Designer, and the snapshot are hand-written from the in-repo templates (Task 1 names them). The WAF tests run `Database.Migrate()`; a model≠snapshot mismatch throws `PendingModelChangesWarning` — they are the migration oracle. Migrations are provider-neutral in `Up()` (no explicit column types, both `Sqlite:Autoincrement`+`Npgsql:ValueGenerationStrategy` where a PK is added — here only columns are added, so `AddColumn` with no explicit type), Designer neutralized (no `HasColumnType`), snapshot SQLite-baked.
- **Fail-closed authorization / fail-open processing:** only members (platform) or visible-project users (WebUI) set statuses; any statistics failure never fails a review, a webhook 200, or the memory. Real cancellation propagates (`when (!ct.IsCancellationRequested)`).
- **Resolution values are strings** (like `Severity`/`Verdict`): status `"Accepted"`|`"Rejected"`, source `"Command"`|`"Checkbox"`|`"Emoji"`|`"WebUi"`|`"Llm"`. PR 3 uses only `Command` and `WebUi`.
- **Precedence (verbatim from spec):** explicit sources (`Command`,`Checkbox`,`Emoji`,`WebUi`) overwrite any existing status; `Llm` writes only when status is null or was itself `Llm`; undo (status→null) only when the current source **is** the source being undone.
- Code comments German; docs English. TDD: red → green → one commit per task.
- Config keys `Naudit:Review:Resolution:Enabled`/`:LlmClassification`/`:RenderCheckbox` join `SettingsCatalog` (non-secret). PR 3 only *reads* `Enabled` (the other two are for PR 4; add all three keys now so the catalog is stable).

---

### Task 1: Migration — resolution + memory-impact columns

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs` (add columns to `ReviewFindingEntity` + `MemoryEntryEntity`)
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260719120000_AddResolutionTracking.cs` + `.Designer.cs`
- Modify: `src/Naudit.Infrastructure/Data/Migrations/NauditDbContextModelSnapshot.cs`
- Test: `tests/Naudit.Tests/ResolutionColumnsTests.cs`

**Interfaces:**
- Produces on `ReviewFindingEntity`: `string? ResolutionStatus`, `string? ResolutionSource`, `string? ResolvedBy`, `DateTime? ResolvedAtUtc`. On `MemoryEntryEntity`: `int TimesApplied` (default 0), `DateTime? LastAppliedAtUtc`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/ResolutionColumnsTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class ResolutionColumnsTests
{
    private static NauditDbContext NewDb()
    {
        var conn = new SqliteConnection("DataSource=:memory:");
        conn.Open();
        var db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return db;
    }

    [Fact]
    public async Task ReviewFinding_persistsResolutionColumns()
    {
        using var db = NewDb();
        var project = new ProjectEntity { PlatformProjectId = "acme/x", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        var review = new ReviewEntity { Project = project, PrNumber = 1, Title = "t", Verdict = "approve", Summary = "s", CreatedAt = DateTime.UtcNow };
        var f = new ReviewFindingEntity
        {
            Review = review, Severity = "high", Confidence = "high", Text = "x",
            ResolutionStatus = "Accepted", ResolutionSource = "Command", ResolvedBy = "bob", ResolvedAtUtc = DateTime.UtcNow,
        };
        db.ReviewFindings.Add(f);
        await db.SaveChangesAsync();

        var loaded = await db.ReviewFindings.SingleAsync();
        Assert.Equal("Accepted", loaded.ResolutionStatus);
        Assert.Equal("Command", loaded.ResolutionSource);
        Assert.Equal("bob", loaded.ResolvedBy);
        Assert.NotNull(loaded.ResolvedAtUtc);
    }

    [Fact]
    public async Task MemoryEntry_hasTimesAppliedDefaultZero_andLastApplied()
    {
        using var db = NewDb();
        var project = new ProjectEntity { PlatformProjectId = "acme/x", FirstReviewedAt = DateTime.UtcNow, LastReviewedAt = DateTime.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        db.MemoryEntries.Add(new MemoryEntryEntity
        {
            ProjectId = project.Id, Kind = "Convention", Text = "c", CreatedBy = "bob", CreatedAt = DateTime.UtcNow, Active = true,
        });
        await db.SaveChangesAsync();

        var m = await db.MemoryEntries.SingleAsync();
        Assert.Equal(0, m.TimesApplied);
        Assert.Null(m.LastAppliedAtUtc);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ResolutionColumnsTests`
Expected: FAIL — columns don't exist (compile error).

- [ ] **Step 3a: Add columns to `Entities.cs`.** In `ReviewFindingEntity` (after `PlatformNoteId`):

```csharp
    /// <summary>Auflösungs-Status des Findings (Review-Analytics): "Accepted" | "Rejected"; null = unbeantwortet.</summary>
    public string? ResolutionStatus { get; set; }
    /// <summary>Woher das Signal kam: "Command" | "Checkbox" | "Emoji" | "WebUi" | "Llm".</summary>
    public string? ResolutionSource { get; set; }
    /// <summary>Plattform-Login bzw. WebUI-Username, der das Finding aufgelöst hat.</summary>
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
```

In `MemoryEntryEntity` (after `Active`):

```csharp
    /// <summary>Wie oft dieser Eintrag in einen Review-Prompt gewählt wurde (CodeRabbits "learnings applied").</summary>
    public int TimesApplied { get; set; }
    public DateTime? LastAppliedAtUtc { get; set; }
```

- [ ] **Step 3b: Hand-written migration** (`20260719120000_AddResolutionTracking.cs`; template for `AddColumn` style: `src/Naudit.Infrastructure/Data/Migrations/20260717230852_AddFindingCommentIds.cs` — that migration added nullable string columns the same way):

```csharp
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Provider-neutral handgepflegt (kein expliziter Typ), wie AddFindingCommentIds.
    /// <inheritdoc />
    public partial class AddResolutionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>("ResolutionStatus", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>("ResolutionSource", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>("ResolvedBy", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<DateTime>("ResolvedAtUtc", "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<int>("TimesApplied", "MemoryEntries", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<DateTime>("LastAppliedAtUtc", "MemoryEntries", nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn("ResolutionStatus", "ReviewFindings");
            migrationBuilder.DropColumn("ResolutionSource", "ReviewFindings");
            migrationBuilder.DropColumn("ResolvedBy", "ReviewFindings");
            migrationBuilder.DropColumn("ResolvedAtUtc", "ReviewFindings");
            migrationBuilder.DropColumn("TimesApplied", "MemoryEntries");
            migrationBuilder.DropColumn("LastAppliedAtUtc", "MemoryEntries");
        }
    }
}
```

- [ ] **Step 3c: Designer + snapshot (hand-edit).**
  - **Designer** (`…AddResolutionTracking.Designer.cs`): copy the header/attribute style of `20260717230852_AddFindingCommentIds.Designer.cs` exactly (`[DbContext(typeof(NauditDbContext))]`, `[Migration("20260719120000_AddResolutionTracking")]`, no `HasColumnType`), content = the full current model **including** the six new properties on the two entities.
  - **Snapshot** (`NauditDbContextModelSnapshot.cs`): in the `ReviewFindingEntity` property block add the four properties (`ResolutionStatus`/`ResolutionSource`/`ResolvedBy` as `b.Property<string>(...).HasColumnType("TEXT")`, `ResolvedAtUtc` as `b.Property<DateTime?>(...).HasColumnType("TEXT")`); in the `MemoryEntryEntity` block add `TimesApplied` (`b.Property<int>("TimesApplied").HasColumnType("INTEGER")`) and `LastAppliedAtUtc` (`b.Property<DateTime?>(...).HasColumnType("TEXT")`). Match the exact style of the neighboring properties in each block.

- [ ] **Step 4: Run the focused test AND the migration oracle**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ResolutionColumnsTests|WebhookEndpointTests"`
Expected: PASS. `WebhookEndpointTests` boots the host → `Database.Migrate()`; green ⇒ migration/Designer/snapshot are consistent with the model. A `PendingModelChangesWarning` failure ⇒ fix the snapshot, not the test.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Data/ tests/Naudit.Tests/ResolutionColumnsTests.cs
git commit -m "feat(analytics): Resolution-Spalten am Finding + TimesApplied/LastApplied am MemoryEntry (Migration AddResolutionTracking)"
```

---

### Task 2: `ResolutionWriter` — precedence + undo logic

**Files:**
- Create: `src/Naudit.Infrastructure/Analytics/ResolutionWriter.cs`
- Test: `tests/Naudit.Tests/ResolutionWriterTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext`, `ReviewFindingEntity` (Task 1).
- Produces: `static Task<bool> ResolutionWriter.ApplyAsync(NauditDbContext db, ReviewFindingEntity finding, string? status, string source, string by, CancellationToken ct = default)` in namespace `Naudit.Infrastructure.Analytics`. Returns `true` when the finding's resolution actually changed (for redelivery-safe confirmations). `status == null` means undo.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Naudit.Tests/ResolutionWriterTests.cs
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
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ResolutionWriterTests`
Expected: FAIL — `ResolutionWriter` doesn't exist.

- [ ] **Step 3: Implement**

```csharp
// src/Naudit.Infrastructure/Analytics/ResolutionWriter.cs
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Analytics;

/// <summary>Schreibt den Resolution-Status eines Findings unter der Präzedenz-Regel (Review-Analytics):
/// explizite Quellen (Command/Checkbox/Emoji/WebUi) überschreiben immer; "Llm" schreibt nur, wenn
/// leer oder selbst zuletzt "Llm"; Undo (status=null) löscht nur, wenn die aktuelle Quelle GENAU die
/// rückgängig gemachte ist. Liefert true, wenn sich etwas geändert hat (redelivery-sichere Bestätigung).</summary>
public static class ResolutionWriter
{
    private const string Llm = "Llm";

    public static async Task<bool> ApplyAsync(
        NauditDbContext db, ReviewFindingEntity finding, string? status, string source, string by, CancellationToken ct = default)
    {
        // Darf diese Quelle den aktuellen Zustand ändern?
        var currentSource = finding.ResolutionSource;
        if (status is null)
        {
            // Undo: nur die eigene Quelle darf löschen; nichts zu tun, wenn schon leer.
            if (finding.ResolutionStatus is null || currentSource != source)
                return false;
        }
        else if (source == Llm)
        {
            // LLM füllt nur Lücken oder korrigiert sich selbst — nie eine explizite Entscheidung.
            if (finding.ResolutionStatus is not null && currentSource != Llm)
                return false;
        }
        // Explizite Quellen überschreiben immer (kein Guard nötig).

        // Keine echte Änderung ⇒ false, ohne SaveChanges.
        if (finding.ResolutionStatus == status
            && (status is null || (finding.ResolutionSource == source)))
            return false;

        finding.ResolutionStatus = status;
        finding.ResolutionSource = status is null ? null : source;
        finding.ResolvedBy = status is null ? null : by;
        finding.ResolvedAtUtc = status is null ? null : DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ResolutionWriterTests`
Expected: PASS (all 6).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Analytics/ResolutionWriter.cs tests/Naudit.Tests/ResolutionWriterTests.cs
git commit -m "feat(analytics): ResolutionWriter mit Präzedenz-/Undo-Regel"
```

---

### Task 3: `ReviewResolutionOptions` + config + SettingsCatalog

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs` (add `Resolution` + options class)
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (three keys)
- Test: `tests/Naudit.Tests/ReviewResolutionOptionsTests.cs`

**Interfaces:**
- Produces: `ReviewOptions.Resolution` of type `ReviewResolutionOptions { bool Enabled = true; bool LlmClassification = true; bool RenderCheckbox = true; }` (namespace `Naudit.Core.Review`). Note: this is Core, but it's a plain options POCO (no SDK) — allowed.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Naudit.Tests/ReviewResolutionOptionsTests.cs
using Microsoft.Extensions.Configuration;
using Naudit.Core.Review;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class ReviewResolutionOptionsTests
{
    [Fact]
    public void Defaults_areOn()
    {
        var o = new ReviewResolutionOptions();
        Assert.True(o.Enabled);
        Assert.True(o.LlmClassification);
        Assert.True(o.RenderCheckbox);
    }

    [Fact]
    public void BindsFromConfig_underReviewResolution()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Review:Resolution:Enabled"] = "false",
        }).Build();
        var opts = cfg.GetSection("Naudit:Review").Get<ReviewOptions>()!;
        Assert.False(opts.Resolution.Enabled);
    }

    [Fact]
    public void CatalogContainsResolutionKeys_allNonSecret()
    {
        foreach (var key in new[]
        {
            "Naudit:Review:Resolution:Enabled",
            "Naudit:Review:Resolution:LlmClassification",
            "Naudit:Review:Resolution:RenderCheckbox",
        })
        {
            Assert.True(SettingsCatalog.TryGet(key, out var def), $"{key} fehlt im Katalog");
            Assert.False(def!.IsSecret);
        }
    }
}
```

(If `SettingsCatalog.TryGet` has a different signature, adapt the assertion to the catalog's actual lookup API — check `SettingsCatalog.cs`.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewResolutionOptionsTests`
Expected: FAIL — `Resolution` property / keys missing.

- [ ] **Step 3a: `ReviewOptions.cs`** — add to `ReviewOptions`:

```csharp
    /// <summary>Finding-Resolution-Tracking (Review-Analytics, Naudit:Review:Resolution).</summary>
    public ReviewResolutionOptions Resolution { get; set; } = new();
```

and append:

```csharp
/// <summary>Review-Analytics: Erfassung der Finding-Auflösung. Enabled=false ⇒ keine Signal-Erfassung
/// (Webhooks antworten trotzdem 200); der Analytics-Endpoint bleibt lesbar.</summary>
public sealed class ReviewResolutionOptions
{
    public bool Enabled { get; set; } = true;
    public bool LlmClassification { get; set; } = true;   // Freitext-Klassifikation (PR 4)
    public bool RenderCheckbox { get; set; } = true;       // GitHub-Checkbox-Footer (PR 4)
}
```

- [ ] **Step 3b: `SettingsCatalog.cs`** — after the memory keys:

```csharp
        new("Naudit:Review:Resolution:Enabled", false),
        new("Naudit:Review:Resolution:LlmClassification", false),
        new("Naudit:Review:Resolution:RenderCheckbox", false),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ReviewResolutionOptionsTests|SettingsEndpointTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Review/ReviewOptions.cs src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/ReviewResolutionOptionsTests.cs
git commit -m "feat(analytics): ReviewResolutionOptions + SettingsCatalog-Keys"
```

---

### Task 4: `@naudit ok` — parser generalization + reply command kind + mappers

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/FpReplyCommand.cs` (recognize `ok`/`angenommen`/`accepted`, return a kind)
- Modify: `src/Naudit.Infrastructure/Git/ReviewCommentReply.cs` (add `ReviewCommandKind Command`)
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs` + `src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs` (`ToCommentReply` sets the kind)
- Test: extend `tests/Naudit.Tests/FpReplyCommandTests.cs`, `GitHubWebhookTests.cs`, `GitLabWebhookTests.cs`

**Interfaces:**
- Produces:
  - `enum ReviewCommandKind { FalsePositive, Accept }` (namespace `Naudit.Infrastructure.Git`).
  - `record ParsedReviewCommand(ReviewCommandKind Kind, string? Reason)` (replaces `ParsedFpCommand`).
  - `FpReplyCommand.TryParse(string?) -> ParsedReviewCommand?` (same class, now both verbs).
  - `ReviewCommentReply` gains trailing `ReviewCommandKind Command` — Task 5 branches on it.

- [ ] **Step 1: Write the failing tests.** Extend `FpReplyCommandTests.cs`:

```csharp
    [Theory]
    [InlineData("@naudit ok")]
    [InlineData("@naudit angenommen")]
    [InlineData("@naudit accepted")]
    [InlineData("@Naudit OK")]
    public void TryParse_recognisesAcceptVerb(string body)
    {
        var cmd = FpReplyCommand.TryParse(body);
        Assert.NotNull(cmd);
        Assert.Equal(ReviewCommandKind.Accept, cmd!.Kind);
    }

    [Fact]
    public void TryParse_fpStaysFalsePositiveKind()
    {
        var cmd = FpReplyCommand.TryParse("@naudit fp weil X");
        Assert.Equal(ReviewCommandKind.FalsePositive, cmd!.Kind);
        Assert.Equal("weil X", cmd.Reason);
    }

    [Fact]
    public void TryParse_okWithTrailingText_keepsReason()
    {
        var cmd = FpReplyCommand.TryParse("@naudit ok danke");
        Assert.Equal(ReviewCommandKind.Accept, cmd!.Kind);
        Assert.Equal("danke", cmd.Reason);
    }
```

Update every existing `FpReplyCommandTests` assertion that used `ParsedFpCommand`/`.Reason` to the new `ParsedReviewCommand` (the `.Reason` accessor stays; add `.Kind == ReviewCommandKind.FalsePositive` where a `fp` command is asserted). Add to `GitHubWebhookTests`/`GitLabWebhookTests` one test each: an `@naudit ok` reply maps to a `ReviewCommentReply` with `Command == ReviewCommandKind.Accept`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FpReplyCommandTests|GitHubWebhookTests|GitLabWebhookTests"`
Expected: FAIL — `ReviewCommandKind`/`ParsedReviewCommand`/`.Command` missing.

- [ ] **Step 3a: `FpReplyCommand.cs`** — generalize:

```csharp
using System.Text.RegularExpressions;

namespace Naudit.Infrastructure.Git;

/// <summary>Art des Antwort-Kommandos an einem Inline-Kommentar.</summary>
public enum ReviewCommandKind { FalsePositive, Accept }

/// <summary>Ein erkanntes Antwort-Kommando: Art + optionaler Grund (Rest der Zeile).</summary>
public sealed record ParsedReviewCommand(ReviewCommandKind Kind, string? Reason);

/// <summary>Parst die Antwort auf einen Inline-Kommentar: "@naudit fp|false-positive &lt;grund&gt;"
/// (⇒ FalsePositive) oder "@naudit ok|angenommen|accepted &lt;text&gt;" (⇒ Accept), case-insensitiv,
/// am Zeilenanfang, Verb durch Whitespace vom Rest getrennt oder Zeilenende. Kein Match ⇒ null.</summary>
public static class FpReplyCommand
{
    private static readonly Regex Pattern = new(
        @"^@naudit\s+(?<verb>fp|false-positive|ok|angenommen|accepted)(?:[ \t]+(?<rest>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ParsedReviewCommand? TryParse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var line = body.Trim();
        var nl = line.IndexOf('\n');
        if (nl >= 0)
            line = line[..nl];
        line = line.TrimEnd('\r').Trim();

        var m = Pattern.Match(line);
        if (!m.Success)
            return null;

        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var kind = verb is "fp" or "false-positive" ? ReviewCommandKind.FalsePositive : ReviewCommandKind.Accept;
        var rest = m.Groups["rest"].Value.Trim();
        return new ParsedReviewCommand(kind, rest.Length == 0 ? null : rest);
    }
}
```

- [ ] **Step 3b: `ReviewCommentReply.cs`** — add a trailing member `ReviewCommandKind Command` to the record (keep it last so existing positional construction sites in the mappers get it explicitly). Update its doc comment.

- [ ] **Step 3c: mappers.** In both `GitHubWebhook.ToCommentReply` and `GitLabWebhook.ToCommentReply`, the local `var cmd = FpReplyCommand.TryParse(...)` now yields a `ParsedReviewCommand`; pass `cmd.Reason` as before and add `Command: cmd.Kind` to the `ReviewCommentReply` construction.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FpReplyCommandTests|GitHubWebhookTests|GitLabWebhookTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/ tests/Naudit.Tests/FpReplyCommandTests.cs tests/Naudit.Tests/GitHubWebhookTests.cs tests/Naudit.Tests/GitLabWebhookTests.cs
git commit -m "feat(analytics): @naudit ok im Command-Parser + ReviewCommandKind am Reply"
```

---

### Task 5: `ReviewCommentCommandService` — branch fp/ok, write resolution, confirm

**Files:**
- Modify: `src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs`
- Modify: DI/registration is unchanged (service already registered). The service needs `ReviewOptions` to read `Resolution.Enabled` — add it as a ctor dependency (it's a registered singleton).
- Test: extend `tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs`

**Interfaces:**
- Consumes: `ResolutionWriter` (Task 2), `ReviewCommandKind` (Task 4), `ReviewOptions.Resolution` (Task 3), existing `MemoryEntryWriter`, `IReviewCommentResponder`.
- Behaviour: after authorization + finding lookup —
  - **FalsePositive:** mark FP via `MemoryEntryWriter` (existing) **and** `ResolutionWriter.ApplyAsync(finding, "Rejected", "Command", author)`; confirm `"Als False Positive gemerkt."` only when the memory entry newly changed (existing redelivery rule).
  - **Accept:** `ResolutionWriter.ApplyAsync(finding, "Accepted", "Command", author)`; confirm `"Als angenommen vermerkt."` only when the resolution actually changed. No memory entry.
  - When `Resolution.Enabled == false`: skip the resolution write on BOTH branches (fp still marks memory + confirms as today; ok becomes a no-op that just logs — nothing to record).

- [ ] **Step 1: Write the failing tests.** Extend `ReviewCommentCommandServiceTests.cs` (its `Reply(...)` helper currently builds a `ReviewCommentReply`; add the `Command` argument — default `FalsePositive` to keep existing tests, and an `accept:` overload). Add:

```csharp
    [Fact]
    public async Task Fp_alsoWritesRejectedResolution()
    {
        using var db = NewDb();
        var finding = await SeedAsync(db, "acme/widgets", "555");
        var responder = new FakeResponder(authorized: true);
        var svc = Service(db, responder);   // helper building the service with ReviewOptions (Resolution.Enabled=true)

        await svc.HandleAsync(Reply("acme/widgets", "555"));   // fp

        var f = await db.ReviewFindings.SingleAsync(x => x.Id == finding.Id);
        Assert.Equal("Rejected", f.ResolutionStatus);
        Assert.Equal("Command", f.ResolutionSource);
        Assert.Single(db.MemoryEntries);   // fp weiterhin im Gedächtnis
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
    }
```

Add a `Service(db, responder, resolutionEnabled = true)` helper that constructs `ReviewCommentCommandService` with a `ReviewOptions` whose `Resolution.Enabled` is set, and `AcceptReply(...)` building a reply with `Command = ReviewCommandKind.Accept`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommentCommandServiceTests`
Expected: FAIL — ctor arity (no `ReviewOptions`) / `AcceptConfirmationText` / accept branch missing.

- [ ] **Step 3: Implement.** Add `ReviewOptions options` as the last ctor parameter. Add `public const string AcceptConfirmationText = "Als angenommen vermerkt.";`. After the finding lookup (`var finding = findings[0];`), branch on `reply.Command`:
  - Factor the confirmation post into a local helper `ConfirmAsync(string text)` (the existing try/catch best-effort block).
  - **FalsePositive branch** (existing memory logic) plus: `if (options.Resolution.Enabled) await ResolutionWriter.ApplyAsync(db, finding, "Rejected", "Command", reply.AuthorLogin, ct);` — keep the existing "confirm only when `result.NewlyMarked`" rule for the FP confirmation.
  - **Accept branch:** `if (!options.Resolution.Enabled) { log; return; }` then `var changed = await ResolutionWriter.ApplyAsync(db, finding, "Accepted", "Command", reply.AuthorLogin, ct); if (changed) await ConfirmAsync(AcceptConfirmationText);` with an info log.

  Keep the `namespace`/`using` additions (`Naudit.Infrastructure.Analytics`, `Naudit.Core.Review`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewCommentCommandServiceTests`
Then `dotnet build Naudit.slnx` (the ctor change may touch the Fp-command DI registration/test construction — fix any call site).
Expected: PASS + build clean.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Memory/ReviewCommentCommandService.cs tests/Naudit.Tests/ReviewCommentCommandServiceTests.cs
git commit -m "feat(analytics): fp schreibt Rejected + @naudit ok schreibt Accepted (Command-Quelle), Resolution-Schalter"
```

---

### Task 6: WebUI resolution action + review-detail surfacing

**Files:**
- Create: `src/Naudit.Web/Endpoints/ResolutionEndpoints.cs` (`PUT /api/findings/{id:int}/resolution`)
- Modify: `src/Naudit.Web/Program.cs` (`app.MapResolutionEndpoints();` next to `app.MapMemoryEndpoints();`)
- Modify: `src/Naudit.Web/Endpoints/DataEndpoints.cs` (add `resolutionStatus` to the `/api/reviews/{id}` findings projection)
- Test: `tests/Naudit.Tests/ResolutionEndpointTests.cs`

**Interfaces:**
- `PUT /api/findings/{id}/resolution` body `{ "status": "Accepted" | "Rejected" | null }` → source `WebUi`, `ResolvedBy` = session user; validates status ∈ {Accepted, Rejected, null}; auth mirrors `MemoryEndpoints` (session → finding → `CanSeeProjectAsync`). Uses `ResolutionWriter`.

- [ ] **Step 1: Write the failing tests.** Model on `tests/Naudit.Tests/MemoryEndpointTests.cs` (login/seed helpers). Cover: `Put_withoutSession_returns401`; `Put_unknownFinding_returns404`; `Put_foreignProject_returns403`; `Put_accepted_thenReviewDetail_showsResolutionStatus` (PUT Accepted, GET `/api/reviews/{reviewId}` → the finding's `resolutionStatus == "Accepted"`); `Put_invalidStatus_returns400` (e.g. `"Maybe"`); `Put_null_undoesOwnWebUi` (set Accepted via WebUi, then null → cleared).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ResolutionEndpointTests`
Expected: FAIL — route unmapped / compile error.

- [ ] **Step 3: Implement `ResolutionEndpoints.cs`** (mirror `MemoryEndpoints` auth exactly):

```csharp
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Analytics;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Resolution-API: Finding im Review-Detail als Accepted/Rejected markieren (Quelle WebUi).
/// Sichtbarkeit wie das Dashboard, 401/403 statt Redirects.</summary>
public static class ResolutionEndpoints
{
    private sealed record ResolutionBody(string? Status);
    private static readonly string[] Valid = ["Accepted", "Rejected"];

    public static void MapResolutionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapPut("/findings/{id:int}/resolution", async (HttpContext ctx, NauditDbContext db, int id, ResolutionBody body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            // status null = Undo; sonst muss er gültig sein.
            if (body.Status is not null && !Valid.Contains(body.Status))
                return Results.BadRequest(new { error = "status must be Accepted, Rejected or null" });

            var finding = await db.ReviewFindings.Include(f => f.Review)
                .SingleOrDefaultAsync(f => f.Id == id, ctx.RequestAborted);
            if (finding is null) return Results.NotFound();
            if (!await CurrentAccount.CanSeeProjectAsync(db, acct, finding.Review.ProjectId, ctx.RequestAborted))
                return Results.Forbid();

            await ResolutionWriter.ApplyAsync(db, finding, body.Status, "WebUi", acct.Username, ctx.RequestAborted);
            return Results.Ok(new { id = finding.Id, resolutionStatus = finding.ResolutionStatus });
        });
    }
}
```

Map it in `Program.cs`. In `DataEndpoints.cs` add `resolutionStatus = f.ResolutionStatus,` to the `findings` projection of `/api/reviews/{id}`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ResolutionEndpointTests|DataEndpointTests|MemoryEndpointTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/ResolutionEndpoints.cs src/Naudit.Web/Program.cs src/Naudit.Web/Endpoints/DataEndpoints.cs tests/Naudit.Tests/ResolutionEndpointTests.cs
git commit -m "feat(analytics): WebUI-Resolution-Endpoint (PUT /api/findings/{id}/resolution) + resolutionStatus im Review-Detail"
```

---

### Task 7: `TimesApplied` increment in `DbReviewMemory`

**Files:**
- Modify: `src/Naudit.Infrastructure/Memory/DbReviewMemory.cs`
- Test: extend `tests/Naudit.Tests/DbReviewMemoryTests.cs`

**Interfaces:**
- Behaviour: when `SelectAsync` picks entries into a prompt, increment each selected entry's `TimesApplied` and set `LastAppliedAtUtc = UtcNow`, then `SaveChangesAsync`. This is best-effort inside the existing fail-open `try`: a write failure must still return the selected entries (the review must not fail because a counter couldn't be bumped). Only the entries actually returned (after the `MaxEntries` cap) are counted.

- [ ] **Step 1: Write the failing test.** Extend `DbReviewMemoryTests.cs`:

```csharp
    [Fact]
    public async Task SelectAsync_incrementsTimesApplied_onSelectedEntries()
    {
        using var db = NewDb();   // reuse the file's existing SQLite helper
        var project = await SeedProjectAsync(db, "acme/x");   // reuse existing seed helper (or inline)
        db.MemoryEntries.Add(new MemoryEntryEntity
        {
            ProjectId = project.Id, Kind = "Convention", Text = "c", CreatedBy = "b", CreatedAt = DateTime.UtcNow, Active = true,
        });
        await db.SaveChangesAsync();

        var sut = new DbReviewMemory(db, OptionsWithMemory(), NullLogger<DbReviewMemory>.Instance);
        await sut.SelectAsync("acme/x", new List<CodeChange> { new("src/A.cs", "@@ +1 @@\n+x") });

        var m = await db.MemoryEntries.SingleAsync();
        Assert.Equal(1, m.TimesApplied);
        Assert.NotNull(m.LastAppliedAtUtc);

        await sut.SelectAsync("acme/x", new List<CodeChange> { new("src/A.cs", "@@ +1 @@\n+x") });
        Assert.Equal(2, (await db.MemoryEntries.SingleAsync()).TimesApplied);
    }
```

(Match the existing `DbReviewMemoryTests` construction of `DbReviewMemory` + its `ReviewOptions` helper; add `SeedProjectAsync` if the file doesn't already have one.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DbReviewMemoryTests`
Expected: FAIL — `TimesApplied` stays 0.

- [ ] **Step 3: Implement.** In `DbReviewMemory.SelectAsync`, after computing the final capped list of selected entities (the ones mapped to `MemoryEntry`), bump the corresponding tracked entities and save — inside the existing `try` (fail-open). Concretely: keep the selected `MemoryEntryEntity` list (before the `.Select(m => new MemoryEntry(...))`), then:

```csharp
            var now = DateTime.UtcNow;
            foreach (var e in selectedEntities)   // die tatsächlich (nach Cap) gewählten Entities
            {
                e.TimesApplied++;
                e.LastAppliedAtUtc = now;
            }
            await db.SaveChangesAsync(ct);   // Best-effort: liegt im vorhandenen fail-open-try
```

Return the mapped `MemoryEntry` list as before. The surrounding `catch` already swallows and returns `[]` — but a save failure after selection would then drop the whole memory for this review. To avoid that, wrap **only** the counter save in its own inner try/catch that logs and continues (the selection result is already computed):

```csharp
            try { await db.SaveChangesAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            { logger.LogWarning(ex, "TimesApplied-Zähler-Update fehlgeschlagen — Auswahl bleibt gültig."); }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DbReviewMemoryTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Memory/DbReviewMemory.cs tests/Naudit.Tests/DbReviewMemoryTests.cs
git commit -m "feat(analytics): TimesApplied/LastAppliedAtUtc beim Gedächtnis-Select hochzählen (best-effort)"
```

---

### Task 8: Analytics endpoint `GET /api/analytics`

**Files:**
- Create: `src/Naudit.Web/Endpoints/AnalyticsEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (`app.MapAnalyticsEndpoints();`)
- Test: `tests/Naudit.Tests/AnalyticsEndpointTests.cs`

**Interfaces:**
- `GET /api/analytics?projectId=&days=30` — `projectId` optional (omitted = all visible), `days` ∈ {7,30,90} default 30, other values → 400. Visibility via `CurrentAccount.VisibleProjects`. Response `{ totals, bySeverity, weekly, memory }` per spec §Analytics.

- [ ] **Step 1: Write the failing tests.** Model on `DataEndpointTests`/`MemoryEndpointTests`. Cover: `Get_withoutSession_returns401`; `Get_invalidDays_returns400`; `Get_totals_computeRatesAndUnanswered` (seed a project+review with findings of mixed `ResolutionStatus` — Accepted/Rejected/null — assert `posted`, `accepted`, `rejected`, `unanswered`, `acceptanceRate`, `fpRate`; incl. a zero-posted project → rates 0); `Get_bySeverity_breaksDown`; `Get_weekly_bucketsByIsoWeek` (two findings in different ISO weeks → two buckets); `Get_foreignProject_notCounted` (a non-admin sees only own projects); `Get_memory_countsEntriesActiveTimesApplied`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter AnalyticsEndpointTests`
Expected: FAIL — route unmapped.

- [ ] **Step 3: Implement `AnalyticsEndpoints.cs`.** Read visible projects (optionally filtered by `projectId` after a visibility check), load their reviews+findings and memory entries, then aggregate **in memory** (finding volumes are small; avoids provider-specific date SQL):

```csharp
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Web.Endpoints;

/// <summary>Auswertungs-API: Acceptance-/FP-Rate, Severity-Breakdown, ISO-Wochen-Trend, Gedächtnis-Wirkung.
/// Sichtbarkeit wie das Dashboard; nur lesend (unabhängig vom Resolution-Schalter).</summary>
public static class AnalyticsEndpoints
{
    private static readonly int[] AllowedDays = [7, 30, 90];

    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();

        api.MapGet("/analytics", async (HttpContext ctx, NauditDbContext db, int? projectId, int days) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Forbid();
            if (days == 0) days = 30;
            if (!AllowedDays.Contains(days))
                return Results.BadRequest(new { error = "days must be 7, 30 or 90" });

            var projectsQuery = CurrentAccount.VisibleProjects(db, acct);
            if (projectId is int pid)
            {
                if (!await projectsQuery.AnyAsync(p => p.Id == pid, ctx.RequestAborted)) return Results.Forbid();
                projectsQuery = projectsQuery.Where(p => p.Id == pid);
            }
            var projects = await projectsQuery
                .Include(p => p.Reviews).ThenInclude(r => r.Findings)
                .ToListAsync(ctx.RequestAborted);

            var since = DateTime.UtcNow.Date.AddDays(-days + 1);
            var findings = projects
                .SelectMany(p => p.Reviews)
                .Where(r => r.CreatedAt.Date >= since)
                .SelectMany(r => r.Findings.Select(f => (r.CreatedAt, f.Severity, f.ResolutionStatus)))
                .ToList();

            int posted = findings.Count;
            int accepted = findings.Count(f => f.ResolutionStatus == "Accepted");
            int rejected = findings.Count(f => f.ResolutionStatus == "Rejected");
            int unanswered = posted - accepted - rejected;
            double Rate(int n) => posted == 0 ? 0 : (double)n / posted;

            string[] severities = ["critical", "high", "medium", "low", "info"];
            var bySeverity = severities.Select(s =>
            {
                var g = findings.Where(f => string.Equals(f.Severity, s, StringComparison.OrdinalIgnoreCase)).ToList();
                return new
                {
                    severity = s,
                    posted = g.Count,
                    accepted = g.Count(f => f.ResolutionStatus == "Accepted"),
                    rejected = g.Count(f => f.ResolutionStatus == "Rejected"),
                };
            }).Where(x => x.posted > 0);

            var weekly = findings
                .GroupBy(f => IsoWeekStart(f.CreatedAt))
                .OrderBy(g => g.Key)
                .Select(g => new
                {
                    weekStart = g.Key.ToString("yyyy-MM-dd"),
                    posted = g.Count(),
                    accepted = g.Count(f => f.ResolutionStatus == "Accepted"),
                    rejected = g.Count(f => f.ResolutionStatus == "Rejected"),
                });

            var projectIds = projects.Select(p => p.Id).ToList();
            var memoryEntries = await db.MemoryEntries.Where(m => projectIds.Contains(m.ProjectId)).ToListAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                totals = new
                {
                    posted, accepted, rejected, unanswered,
                    acceptanceRate = Rate(accepted), fpRate = Rate(rejected),
                },
                bySeverity,
                weekly,
                memory = new
                {
                    entries = memoryEntries.Count,
                    active = memoryEntries.Count(m => m.Active),
                    timesApplied = memoryEntries.Sum(m => m.TimesApplied),
                },
            });
        });
    }

    // ISO-8601-Wochenstart (Montag) des Datums — provider-neutral in-memory.
    private static DateTime IsoWeekStart(DateTime dt)
    {
        var d = dt.Date;
        int diff = ((int)d.DayOfWeek + 6) % 7;   // Montag=0 … Sonntag=6
        return d.AddDays(-diff);
    }
}
```

Map it in `Program.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter AnalyticsEndpointTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Web/Endpoints/AnalyticsEndpoints.cs src/Naudit.Web/Program.cs tests/Naudit.Tests/AnalyticsEndpointTests.cs
git commit -m "feat(analytics): GET /api/analytics (Totals/Raten, Severity-Breakdown, ISO-Wochen-Trend, Memory-Wirkung)"
```

---

### Task 9: "Auswertung" page (frontend) + resolution buttons in the review detail

**Files:**
- Modify: `src/frontend/src/App.tsx` (`AppPage` union + route), `src/frontend/src/components/TopBar.tsx` (nav button)
- Create: `src/frontend/src/components/pages/AnalyticsPage.tsx`
- Modify: `src/frontend/src/hooks/queries.ts` (+ `useAnalytics`), `src/frontend/src/hooks/mutations.ts` (+ `useSetResolution`), the types module (add `AnalyticsDto`), and the review-detail component that lists findings (add Accept/Reject buttons + reuse the FP action for Reject)
- Modify: `src/frontend/src/components/ReviewDetail.tsx` (resolution buttons per finding)

**Interfaces:**
- Consumes Task 8's `/api/analytics` and Task 6's `/api/findings/{id}/resolution`. `AnalyticsDto` matches the endpoint's JSON.

- [ ] **Step 1: Types + hooks.** Mirror the existing `useDashboard`/`useProjectMemory` (queries) and `useCreateConvention` (mutations) idioms exactly:

```typescript
// queries.ts
export function useAnalytics(projectId: number | null, days: number) {
  return useQuery({
    queryKey: ["analytics", projectId, days],
    queryFn: () => api<AnalyticsDto>(`/api/analytics?days=${days}${projectId ? `&projectId=${projectId}` : ""}`),
  });
}

// mutations.ts
export function useSetResolution(reviewId: number | null) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (vars: { findingId: number; status: "Accepted" | "Rejected" | null }) =>
      api<{ id: number; resolutionStatus: string | null }>(`/api/findings/${vars.findingId}/resolution`, {
        method: "PUT",
        body: JSON.stringify({ status: vars.status }),
      }),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["review", reviewId] }),
  });
}
```

Add `AnalyticsDto` to the types module (fields: `totals {posted,accepted,rejected,unanswered,acceptanceRate,fpRate}`, `bySeverity: {severity,posted,accepted,rejected}[]`, `weekly: {weekStart,posted,accepted,rejected}[]`, `memory {entries,active,timesApplied}`).

- [ ] **Step 2: `AnalyticsPage.tsx`.** New page using `StatTile` (posted / acceptance-rate / fp-rate / memory-applied), CSS/SVG bars for `bySeverity` (posted vs accepted per severity), a `Sparkline` for `weekly.map(w => w.posted)` (and/or acceptance ratio), a 7/30/90 range selector and the dashboard's project `<select>` (reuse the `MemoryPage` project-select idiom). Rendered from the existing UI kit only — **no new frontend dependency**. Guard loading/empty like the other pages.

- [ ] **Step 3: Wire nav.** In `App.tsx` add `"analytics"` to the `AppPage` union, import + render `AnalyticsPage` for `page === "analytics"`. In `TopBar.tsx` add a nav button `Auswertung` (visible to all authenticated users, like `Memory`). In `ReviewDetail.tsx` add per-finding **Accept** / **Reject** buttons driving `useSetResolution`; **Reject** additionally offers the existing "mark false positive" action in the same spot (two clicks, not automatic); show the current `resolutionStatus` (e.g. a `Pill`).

- [ ] **Step 4: Verify**

Run: `cd src/frontend && npm ci && npm run lint && npm run build`
Expected: lint clean, `tsc --noEmit` + vite build succeed.

- [ ] **Step 5: Commit**

```bash
git add src/frontend/src
git commit -m "feat(analytics): Auswertung-Seite (StatTiles/Sparkline) + Accept/Reject im Review-Detail"
```

---

### Task 10: Docs + full-suite gate

**Files:**
- Create: `docs/review-analytics.md`
- Modify: `CLAUDE.md` (extension-point bullet + request-flow note that memory selection now bumps `TimesApplied`)

- [ ] **Step 1: Write `docs/review-analytics.md`** (English): what it tracks (resolution status per finding), the signal sources shipped in PR 3 (`@naudit ok`/`@naudit fp` commands, WebUI Accept/Reject) and the precedence rule, the `GET /api/analytics` contract (params + response), the "Auswertung" page, `TimesApplied` memory impact, the `Naudit:Review:Resolution:*` config keys, and a "PR 4 (LLM classification, GitHub checkbox, GitLab emoji) — planned" outlook. Verify every claim against the code.

- [ ] **Step 2: Update `CLAUDE.md`** — add a "Review analytics" extension-point bullet (resolution columns, `ResolutionWriter` precedence, `@naudit ok`, analytics endpoint/page, `Naudit:Review:Resolution:Enabled`), and note in the request-flow that `DbReviewMemory` selection now increments `TimesApplied`.

- [ ] **Step 3: Full-suite gate**

Run: `dotnet test Naudit.slnx` — all green (a rare pre-existing `GitWorkspaceProviderTests` /tmp-flake is unrelated; if exactly that fails, re-run to confirm — any other failure is real). Then `cd src/frontend && npm run lint && npm run build` — clean.

- [ ] **Step 4: Commit**

```bash
git add docs/review-analytics.md CLAUDE.md
git commit -m "docs(analytics): Review-Analytics PR 3 dokumentiert (Signale, Präzedenz, API, Config)"
```

---

## Self-Review

**1. Spec coverage** (`2026-07-17-review-analytics-design.md`, PR 3 slice):
- Migration: resolution columns + `TimesApplied`/`LastAppliedAtUtc` → Task 1. `PlatformNoteId` **omitted** (already added in PR 2a/#62 — a deliberate reality-delta vs. the spec, which predates that PR; documented here). ✅
- Precedence/undo logic → Task 2 (`ResolutionWriter`) + tests for the full matrix. ✅
- `@naudit ok` command (aliases, same parser) → Task 4; confirmation "Als angenommen vermerkt." + fp→Rejected/ok→Accepted → Task 5. ✅
- WebUI resolution actions (`PUT /api/findings/{id}/resolution`, source WebUi, Reject offers FP) → Task 6 (backend) + Task 9 (buttons). ✅
- `TimesApplied` increment on selection → Task 7. ✅
- Analytics endpoint (totals/rates incl. posted=0, severity, ISO-weekly, memory; visibility; days validation) → Task 8. ✅
- "Auswertung" page from the existing UI kit → Task 9. ✅
- `ReviewResolutionOptions` + `SettingsCatalog`; `Enabled=false` skips capture but keeps analytics readable → Tasks 3 + 5 (gate) + 8 (analytics unaffected). ✅
- Docs + CLAUDE.md → Task 10. ✅
- Explicitly **out of PR 3** (→ PR 4): LLM classification, GitHub checkbox `edited`, GitLab emoji events + wizard `emoji_events`. Not planned here. ✅

**2. Placeholder scan:** logic-bearing code (ResolutionWriter, parser, analytics aggregation, endpoints) is complete; the mechanical/mirrored parts (migration Designer/snapshot, endpoint auth, frontend page) name the exact in-repo file to mirror and enumerate the concrete cases — no TBDs.

**3. Type consistency:** `ResolutionWriter.ApplyAsync(db, finding, string? status, string source, string by, ct)` identical in Tasks 2/5/6. `ReviewCommandKind`/`ParsedReviewCommand`/`ReviewCommentReply.Command` consistent in Tasks 4/5. Resolution string values (`"Accepted"`/`"Rejected"`/`"Command"`/`"WebUi"`) identical across writer, service, endpoints, analytics. `AnalyticsDto` shape (Task 9) matches the endpoint JSON (Task 8). Config `Naudit:Review:Resolution:Enabled` consistent in Tasks 3/5.
