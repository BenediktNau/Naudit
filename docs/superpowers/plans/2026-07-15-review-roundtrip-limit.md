# Review-Roundtrip-Limit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit reviewt nur noch bei echten Pushes (GitLab-`oldrev`-Filter) und maximal N-mal pro PR (Default 3, `Naudit:Review:MaxRoundtrips`, Settings-editierbar; CI-Trigger ausgenommen).

**Architecture:** Scheibe A filtert GitLabs `update`-Action auf Events mit `oldrev` (nur echte Pushes tragen es; GitHub ist via Action-Whitelist schon korrekt). Scheibe B zählt gepostete Reviews über einen neuen Core-Seam `IReviewRoundtripCounter` (EF-Impl zählt vorhandene `ReviewEntity`-Zeilen — keine Migration) und skippt früh im `ReviewService`, bevor Platform-Call/Checkout/LLM anfallen; `ReviewRequest.Trigger` (`Webhook`|`Ci`) nimmt den CI-Pfad aus. Das N-te Review-Summary trägt eine Hinweiszeile; Skips danach sind still (Log im `ReviewBackgroundService` via `ReviewResult.Skipped`).

**Tech Stack:** .NET 10 Minimal API, EF Core (SQLite/Postgres), xUnit, React/TS (Settings-UI).

**Spec:** `docs/superpowers/specs/2026-07-15-review-roundtrip-limit-design.md`

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — `dotnet test Naudit.slnx`, **nie** `Naudit.sln`.
- Core-Regel: `Naudit.Core` kennt nur MEAI-Abstractions — Interface in Core, EF-Impl in Infrastructure.
- Code-Kommentare auf Deutsch; `docs/` auf Englisch.
- Keine DB-Migration in diesem Feature (der Zähler nutzt vorhandene Tabellen).
- Fail-open beim Zählerfehler: DB weg ⇒ Review läuft.
- TDD: erst roter Test, dann Implementierung; ein Commit pro Task.
- Basis ist `main` ohne PR #53 (Author-Sessions). Falls #53 vorher gemergt wird: `ReviewRequest` hat dann bereits `AuthorLogin` als 4. positionalen Parameter und `ReviewService` einen `IAiClientRouter` statt `IChatClient` — `Trigger` dahinter anfügen bzw. Konstruktor-Aufrufe entsprechend anpassen; die Logik dieses Plans ändert sich nicht.

---

### Task 1: Scheibe A — GitLab reviewt `update` nur bei echten Pushes

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (Klasse `GitLabMergeRequestAttributes`)
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs`
- Test: `tests/Naudit.Tests/GitLabWebhookTests.cs`, `tests/Naudit.Tests/GitHubWebhookTests.cs`

**Interfaces:**
- Consumes: bestehendes `GitLabWebhook.ToReviewRequest(GitLabWebhookPayload)`.
- Produces: `GitLabMergeRequestAttributes.OldRev` (`string?`, JSON `oldrev`). Mapping-Verhalten: `action=="update"` ohne `oldrev` ⇒ `null`.

- [ ] **Step 1: Failing Tests schreiben**

In `tests/Naudit.Tests/GitLabWebhookTests.cs` ergänzen:

```csharp
[Fact]
public void ToReviewRequest_ignoresUpdate_withoutNewCommits()
{
    // "update" feuert auch bei Label-/Beschreibungs-/Assignee-Änderungen — ohne oldrev kein Review.
    var json = """{ "object_kind": "merge_request", "project": { "id": 7 }, "object_attributes": { "iid": 42, "title": "x", "action": "update" } }""";
    var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;
    Assert.Null(GitLabWebhook.ToReviewRequest(payload));
}

[Fact]
public void ToReviewRequest_mapsUpdate_withNewCommits()
{
    // oldrev ist nur gesetzt, wenn wirklich Commits gepusht wurden — dann wird reviewt.
    var json = """{ "object_kind": "merge_request", "project": { "id": 7 }, "object_attributes": { "iid": 42, "title": "x", "action": "update", "oldrev": "abc123" } }""";
    var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;

    var request = GitLabWebhook.ToReviewRequest(payload);

    Assert.NotNull(request);
    Assert.Equal(42, request!.MergeRequestIid);
}
```

In `tests/Naudit.Tests/GitHubWebhookTests.cs` ergänzen (Bestätigung, dass Metadaten-Actions nie mappen — neben dem bestehenden `closed`-Test):

```csharp
[Fact]
public void ToReviewRequest_ignoresLabeledAction()
{
    // Kommentare sind eigene Event-Typen (issue_comment) und fallen am eventType-Filter raus;
    // Metadaten-Actions wie "labeled" scheitern an der Whitelist. Kein Review ohne neue Commits.
    var json = """{ "action": "labeled", "repository": { "full_name": "o/r" }, "pull_request": { "number": 1, "title": "x" } }""";
    var payload = JsonSerializer.Deserialize<GitHubWebhookPayload>(json)!;
    Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload));
}
```

- [ ] **Step 2: Tests laufen lassen — rot (die zwei GitLab-Tests)**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitLabWebhookTests|GitHubWebhookTests"`
Expected: `ToReviewRequest_ignoresUpdate_withoutNewCommits` FAIL (mappt heute), `ToReviewRequest_mapsUpdate_withNewCommits` PASS, GitHub-Test PASS.

- [ ] **Step 3: Implementierung**

`src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs`, in `GitLabMergeRequestAttributes` ergänzen:

```csharp
    [JsonPropertyName("oldrev")] public string? OldRev { get; set; }
```

`src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs`, in `ToReviewRequest` nach dem Action-Whitelist-Check:

```csharp
        if (attrs.Action is null || !ReviewableActions.Contains(attrs.Action))
            return null;

        // "update" feuert auch bei Label-/Beschreibungs-/Assignee-Änderungen. Reviewt wird nur,
        // wenn wirklich Commits gepusht wurden — GitLab setzt oldrev genau dann.
        if (attrs.Action == "update" && attrs.OldRev is null)
            return null;
```

- [ ] **Step 4: Tests laufen lassen — grün**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitLabWebhookTests|GitHubWebhookTests"`
Expected: alle PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitLab/ tests/Naudit.Tests/GitLabWebhookTests.cs tests/Naudit.Tests/GitHubWebhookTests.cs
git commit -m "feat(gitlab): update-Events nur mit oldrev (echten Pushes) reviewen"
```

---

### Task 2: Roundtrip-Counter-Seam + EF-Implementierung + DI-Registrierung

**Files:**
- Create: `src/Naudit.Core/Abstractions/IReviewRoundtripCounter.cs`
- Create: `src/Naudit.Infrastructure/Ui/EfReviewRoundtripCounter.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (nach der `IReviewAuditSink`-Registrierung, ~Zeile 185)
- Test: `tests/Naudit.Tests/EfReviewRoundtripCounterTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext.Reviews`/`Projects` (bestehend), `EfReviewAuditSink` (Test-Seeding).
- Produces: `IReviewRoundtripCounter` mit `Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default)` — Task 3 injiziert es in den `ReviewService`. DI: scoped, immer registriert (DB ist immer an).

- [ ] **Step 1: Failing Test schreiben**

`tests/Naudit.Tests/EfReviewRoundtripCounterTests.cs` (neu — DB-Muster wie `EfReviewAuditSinkTests`):

```csharp
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
```

- [ ] **Step 2: Test laufen lassen — rot (Compile-Fehler: Typen fehlen)**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter EfReviewRoundtripCounterTests`
Expected: FAIL — `EfReviewRoundtripCounter` existiert nicht.

- [ ] **Step 3: Interface + Implementierung + Registrierung**

`src/Naudit.Core/Abstractions/IReviewRoundtripCounter.cs` (neu):

```csharp
namespace Naudit.Core.Abstractions;

/// <summary>Zählt bereits gepostete Reviews eines MR/PR — Basis des Roundtrip-Limits
/// (Naudit:Review:MaxRoundtrips). DB-gestützte Implementierung in Infrastructure.</summary>
public interface IReviewRoundtripCounter
{
    Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default);
}
```

`src/Naudit.Infrastructure/Ui/EfReviewRoundtripCounter.cs` (neu):

```csharp
using Microsoft.EntityFrameworkCore;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Der Audit-Log ist der Zähler: gezählt werden die vorhandenen ReviewEntity-Zeilen
/// pro (Projekt, PR). No-Op-Läufe (keine Änderungen ⇒ kein Audit) zählen damit automatisch nie.</summary>
public sealed class EfReviewRoundtripCounter(NauditDbContext db) : IReviewRoundtripCounter
{
    public Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default)
        => db.Reviews.CountAsync(
            r => r.Project.PlatformProjectId == projectId && r.PrNumber == mergeRequestIid, ct);
}
```

`src/Naudit.Infrastructure/DependencyInjection.cs` — direkt nach `services.AddScoped<IReviewAuditSink, EfReviewAuditSink>();` einfügen:

```csharp
        services.AddScoped<IReviewRoundtripCounter, EfReviewRoundtripCounter>();
```

- [ ] **Step 4: Test laufen lassen — grün**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter EfReviewRoundtripCounterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Abstractions/IReviewRoundtripCounter.cs src/Naudit.Infrastructure/Ui/EfReviewRoundtripCounter.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/EfReviewRoundtripCounterTests.cs
git commit -m "feat(core,infra): IReviewRoundtripCounter-Seam + EF-Zähler über den Audit-Log"
```

---

### Task 3: Roundtrip-Gate im ReviewService

**Files:**
- Modify: `src/Naudit.Core/Models/ReviewRequest.cs` (Enum + Trigger-Parameter)
- Modify: `src/Naudit.Core/Models/ReviewResult.cs` — die Datei, die `record ReviewResult` enthält (`Skipped`-Flag)
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs` (`MaxRoundtrips`)
- Modify: `src/Naudit.Core/Review/ReviewService.cs` (Konstruktor + Gate + Hinweiszeile)
- Create: `tests/Naudit.Tests/Fakes/FakeRoundtripCounter.cs`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `IReviewRoundtripCounter.CountAsync(string, int, CancellationToken)` aus Task 2.
- Produces:
  - `public enum ReviewTrigger { Webhook, Ci }` (in `ReviewRequest.cs`, Namespace `Naudit.Core.Models`)
  - `ReviewRequest(string ProjectId, int MergeRequestIid, string Title, ReviewTrigger Trigger = ReviewTrigger.Webhook)` — Task 4 setzt `Ci` im `/review`-Endpoint.
  - `ReviewResult(string Markdown, ReviewVerdict Verdict, bool Skipped = false)` — Task 4 loggt darauf.
  - `ReviewOptions.MaxRoundtrips` (`int`, Default `3`, `<= 0` = unbegrenzt) — bindet automatisch aus `Naudit:Review` (bestehendes `GetSection`-Binding in `DependencyInjection.cs`).

- [ ] **Step 1: Fake + failing Tests schreiben**

`tests/Naudit.Tests/Fakes/FakeRoundtripCounter.cs` (neu):

```csharp
using Naudit.Core.Abstractions;

namespace Naudit.Tests.Fakes;

internal sealed class FakeRoundtripCounter(int count = 0, bool throws = false) : IReviewRoundtripCounter
{
    public int CallCount { get; private set; }

    public Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default)
    {
        CallCount++;
        if (throws) throw new InvalidOperationException("DB nicht erreichbar");
        return Task.FromResult(count);
    }
}
```

In `tests/Naudit.Tests/ReviewServiceTests.cs` die `CreateService`-Helper-Signatur um einen optionalen Parameter erweitern (letzter Parameter, Default = Counter mit 0):

```csharp
    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null,
        IPromptRedactor? redactor = null,
        IContextCollector? contextCollector = null,
        IReviewRoundtripCounter? roundtrips = null)
        => new(chat, git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector(),
            new FakeReviewAuditSink(),
            roundtrips ?? new FakeRoundtripCounter());
```

Neue Tests ergänzen (JSON-Antworten im Stil der bestehenden Tests):

```csharp
    [Fact]
    public async Task ReviewAsync_skipsWebhookReview_whenRoundtripLimitReached()
    {
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 3 },
            roundtrips: new FakeRoundtripCounter(count: 3));

        var result = await service.ReviewAsync(Request);

        Assert.True(result.Skipped);
        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Null(git.PostedMarkdown);          // nichts gepostet …
        Assert.Null(chat.LastMessages);           // … und das LLM nie befragt
    }

    [Fact]
    public async Task ReviewAsync_ciTrigger_reviewsDespiteLimit()
    {
        // Das CI-Gate braucht immer ein frisches Verdict — POST /review ist nie limitiert.
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var counter = new FakeRoundtripCounter(count: 99);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 3 }, roundtrips: counter);

        var result = await service.ReviewAsync(Request with { Trigger = ReviewTrigger.Ci });

        Assert.False(result.Skipped);
        Assert.NotNull(git.PostedMarkdown);
        Assert.Equal(0, counter.CallCount);       // Ci fragt den Zähler gar nicht erst
    }

    [Fact]
    public async Task ReviewAsync_zeroMaxRoundtrips_meansUnlimited()
    {
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var counter = new FakeRoundtripCounter(count: 99);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 0 }, roundtrips: counter);

        var result = await service.ReviewAsync(Request);

        Assert.False(result.Skipped);
        Assert.Equal(0, counter.CallCount);       // Limit aus ⇒ kein Zähler-Roundtrip
    }

    [Fact]
    public async Task ReviewAsync_appendsLimitNotice_onLastAllowedReview()
    {
        // count=2, Limit=3 ⇒ dieses Review ist Nr. 3 (das letzte) und trägt den Hinweis.
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 3 },
            roundtrips: new FakeRoundtripCounter(count: 2));

        await service.ReviewAsync(Request);

        Assert.Contains("Roundtrip-Limit erreicht (3/3)", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_noLimitNotice_beforeLastReview()
    {
        // count=0, Limit=3 ⇒ Review Nr. 1 — noch kein Hinweis.
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 3 },
            roundtrips: new FakeRoundtripCounter(count: 0));

        await service.ReviewAsync(Request);

        Assert.DoesNotContain("Roundtrip-Limit", git.PostedMarkdown!);
    }

    [Fact]
    public async Task ReviewAsync_reviewsNormally_whenCounterThrows()
    {
        // Fail-open: das Review ist der Wert, das Limit nur die Kostenbremse.
        var chat = new FakeChatClient("""{"summary":"## Review","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { MaxRoundtrips = 3 },
            roundtrips: new FakeRoundtripCounter(throws: true));

        var result = await service.ReviewAsync(Request);

        Assert.False(result.Skipped);
        Assert.NotNull(git.PostedMarkdown);
    }
```

Benötigte usings sind in der Testdatei bereits vorhanden (`Naudit.Core.Abstractions`, `Naudit.Core.Models`, `Naudit.Core.Review`, `Naudit.Tests.Fakes`).

- [ ] **Step 2: Tests laufen lassen — rot (Compile-Fehler: Trigger/Skipped/MaxRoundtrips fehlen)**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: FAIL (Build-Fehler).

- [ ] **Step 3: Modelle + Service implementieren**

`src/Naudit.Core/Models/ReviewRequest.cs`:

```csharp
namespace Naudit.Core.Models;

/// <summary>Woher ein Review angestoßen wurde. Webhook-Reviews unterliegen dem Roundtrip-Limit;
/// der synchrone CI-Trigger (POST /review) nie — das Merge-Gate braucht immer ein frisches Verdict.</summary>
public enum ReviewTrigger { Webhook, Ci }

/// <summary>Identifiziert den zu reviewenden Merge Request.</summary>
public sealed record ReviewRequest(string ProjectId, int MergeRequestIid, string Title,
    ReviewTrigger Trigger = ReviewTrigger.Webhook);

/// <summary>Eine geänderte Datei mit ihrem unified diff.</summary>
public sealed record CodeChange(string FilePath, string Diff);
```

In der Datei mit `record ReviewResult` (`src/Naudit.Core/Models/ReviewResult.cs`):

```csharp
/// <summary>Ergebnis eines Reviews: der gepostete Markdown-Text plus das Urteil.
/// Skipped: Review wurde wegen des Roundtrip-Limits übersprungen (nichts gepostet).</summary>
public sealed record ReviewResult(string Markdown, ReviewVerdict Verdict, bool Skipped = false);
```

In `src/Naudit.Core/Review/ReviewOptions.cs`, Klasse `ReviewOptions` ergänzen:

```csharp
    /// <summary>Max. automatische (Webhook-)Reviews pro MR/PR; danach werden Pushes übersprungen.
    /// 0 (oder negativ) = unbegrenzt. Der CI-Trigger (POST /review) ist nie limitiert.</summary>
    public int MaxRoundtrips { get; set; } = 3;
```

`src/Naudit.Core/Review/ReviewService.cs`:

Konstruktor-Parameter anfügen (nach `IReviewAuditSink auditSink`):

```csharp
    IReviewAuditSink auditSink,
    IReviewRoundtripCounter roundtripCounter)
```

Am Anfang von `ReviewAsync` (vor `GetChangesAsync`):

```csharp
        // Roundtrip-Limit: nur Webhook-Reviews drosseln — das CI-Gate braucht immer ein frisches
        // Verdict und ist zugleich der Weg, ein weiteres Review zu erzwingen. Der Zähler sind die
        // bereits geposteten Reviews (Audit-Zeilen); fail-open bei Zählerfehlern.
        var priorReviews = -1; // -1 = Limit inaktiv (Ci-Trigger oder MaxRoundtrips <= 0)
        if (request.Trigger == ReviewTrigger.Webhook && options.MaxRoundtrips > 0)
        {
            priorReviews = await SafeCountRoundtripsAsync(request, ct);
            if (priorReviews >= options.MaxRoundtrips)
                return new ReviewResult(string.Empty, ReviewVerdict.Approve, Skipped: true);
        }
```

Beim Summary-Bau (bestehende Zeile ersetzen):

```csharp
        var lastRoundtrip = priorReviews >= 0 && priorReviews + 1 == options.MaxRoundtrips;
        var summary = ComposeSummary(parsed.Summary, verdict, inline.Count, orphans, lastRoundtrip);
```

`ComposeSummary` um den Parameter + die Schlusszeile erweitern (Signatur + Ende des Methodenrumpfs, vor `return`):

```csharp
    private string ComposeSummary(string? llmSummary, ReviewVerdict verdict, int inlineCount,
        IReadOnlyList<OrphanComment> orphans, bool lastRoundtrip)
    {
        // … bestehender Rumpf unverändert …
        if (lastRoundtrip)
        {
            sb.AppendLine();
            sb.AppendLine($"_ℹ️ Roundtrip-Limit erreicht ({options.MaxRoundtrips}/{options.MaxRoundtrips}) — weitere Pushes an diesem PR werden nicht mehr automatisch reviewt._");
        }
        return sb.ToString().TrimEnd();
    }
```

Neue private Methode (neben `SafeCollectContextAsync`):

```csharp
    // Fail-open: ein Zählerfehler (DB weg) darf das Review nicht verhindern — Count 0 heißt
    // "Limit greift nicht", das Review läuft.
    private async Task<int> SafeCountRoundtripsAsync(ReviewRequest request, CancellationToken ct)
    {
        try { return await roundtripCounter.CountAsync(request.ProjectId, request.MergeRequestIid, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return 0; }
    }
```

- [ ] **Step 4: Tests laufen lassen — grün (inkl. Bestandstests)**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: alle PASS (die bestehenden Tests laufen über `CreateService` mit Default-Counter 0 unverändert grün).

- [ ] **Step 5: Full Suite (DI-Auflösung der neuen Ctor-Abhängigkeit prüfen)**

Run: `dotnet test Naudit.slnx`
Expected: alle PASS — die Wiring-Tests lösen `ReviewService` jetzt mit dem in Task 2 registrierten `EfReviewRoundtripCounter` auf.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/ tests/Naudit.Tests/Fakes/FakeRoundtripCounter.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(core): Roundtrip-Limit im ReviewService — Skip nach N Reviews, Hinweis im letzten Summary"
```

---

### Task 4: Web-Verkabelung — CI-Trigger markieren + Skip-Log

**Files:**
- Modify: `src/Naudit.Web/Program.cs` (`/review`-Endpoint, ~Zeile 327)
- Modify: `src/Naudit.Web/ReviewBackgroundService.cs`

**Interfaces:**
- Consumes: `ReviewTrigger.Ci`, `ReviewResult.Skipped` aus Task 3.
- Produces: keine neuen Schnittstellen — reines Wiring.

- [ ] **Step 1: `/review` als CI-Trigger markieren**

In `src/Naudit.Web/Program.cs` die Zeile

```csharp
            var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty);
```

ersetzen durch:

```csharp
            // CI-Trigger: nie vom Roundtrip-Limit gedrosselt — das Merge-Gate braucht immer ein
            // frisches Verdict (und ist der Weg, ein weiteres Review zu erzwingen).
            var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty,
                ReviewTrigger.Ci);
```

- [ ] **Step 2: Skip-Log im Hintergrund-Consumer**

In `src/Naudit.Web/ReviewBackgroundService.cs` den Aufruf erweitern:

```csharp
                using var scope = scopeFactory.CreateScope();
                var reviewService = scope.ServiceProvider.GetRequiredService<ReviewService>();
                var result = await reviewService.ReviewAsync(request, stoppingToken);
                if (result.Skipped)
                    logger.LogInformation("Review für {ProjectId}#{Iid} übersprungen — Roundtrip-Limit erreicht.",
                        request.ProjectId, request.MergeRequestIid);
```

(Nur Observability — der Log-Text wird nicht separat getestet; das Skip-Verhalten selbst ist in Task 3 abgedeckt.)

- [ ] **Step 3: Full Suite laufen lassen**

Run: `dotnet test Naudit.slnx`
Expected: alle PASS (u. a. `ReviewEndpointTests` unverändert grün — der Endpoint-Kontrakt ist gleich geblieben).

- [ ] **Step 4: Commit**

```bash
git add src/Naudit.Web/Program.cs src/Naudit.Web/ReviewBackgroundService.cs
git commit -m "feat(web): POST /review als Ci-Trigger vom Roundtrip-Limit ausnehmen + Skip-Log"
```

---

### Task 5: Settings — Catalog-Eintrag + Feld in der Review-Kategorie

**Files:**
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `src/frontend/src/components/settings/categories/ReviewCategory.tsx`

**Interfaces:**
- Consumes: bestehenden `SettingsCtx` (`ctx.get`/`ctx.set`/`ctx.locked`), `Panel`/`Field`-Komponenten.
- Produces: DB-verwaltbaren Key `Naudit:Review:MaxRoundtrips` (nicht secret) — via Settings-UI editierbar, Restart-Banner wie üblich.

- [ ] **Step 1: Catalog-Eintrag**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` nach `new("Naudit:Review:Gate:MinConfidence", false),` einfügen:

```csharp
        new("Naudit:Review:MaxRoundtrips", false),
```

- [ ] **Step 2: Frontend-Feld**

In `src/frontend/src/components/settings/categories/ReviewCategory.tsx`:

Nach `const prompt = ctx.get("Naudit:Review:SystemPrompt");` ergänzen:

```tsx
  const roundtrips = ctx.get("Naudit:Review:MaxRoundtrips");
```

Zwischen dem „Merge gate“- und dem „Review prompt“-Panel ein neues Panel einfügen:

```tsx
      <Panel title="Roundtrip limit">
        <div className="px-5 py-4">
          <Field label="Max automatic reviews per PR"
            hint="Further pushes are skipped after this many reviews. 0 = unlimited. CI-triggered reviews (POST /review) are never limited.">
            <input type="number" min={0} placeholder="3 (default)"
              disabled={ctx.locked("Naudit:Review:MaxRoundtrips")}
              className={selCls} value={roundtrips}
              onChange={(e) => ctx.set("Naudit:Review:MaxRoundtrips", e.target.value)} />
          </Field>
        </div>
      </Panel>
```

- [ ] **Step 3: Backend-Suite + Frontend-Build prüfen**

Run: `dotnet test Naudit.slnx && cd src/frontend && npm run lint && npm run build && cd ../..`
Expected: Tests PASS, Lint sauber, Build grün (`tsc --noEmit` + vite).

- [ ] **Step 4: Commit**

```bash
git add src/Naudit.Infrastructure/Settings/SettingsCatalog.cs src/frontend/src/components/settings/categories/ReviewCategory.tsx
git commit -m "feat(settings): Naudit:Review:MaxRoundtrips DB-verwaltbar + Feld in der Review-Kategorie"
```

---

### Task 6: Doku — configuration.md + CLAUDE.md

**Files:**
- Modify: `docs/configuration.md` (Review-Tabelle, nach der `Gate:MinConfidence`-Zeile)
- Modify: `docs/ci-integration.md` (Hinweis auf die CI-Ausnahme)
- Modify: `CLAUDE.md` (Request-flow-Absatz)

**Interfaces:** keine — reine Doku (Englisch in `docs/`).

- [ ] **Step 1: configuration.md**

In der Review-Tabelle nach der `Naudit:Review:Gate:MinConfidence`-Zeile einfügen:

```markdown
| `Naudit:Review:MaxRoundtrips` | Max automatic (webhook-triggered) reviews per MR/PR; further pushes are skipped and the last allowed review notes it in its summary — `0` = unlimited (default `3`). The synchronous CI trigger `POST /review` is never limited. |
```

- [ ] **Step 2: ci-integration.md**

An passender Stelle (nach der Beschreibung des Endpoints) einen Satz ergänzen:

```markdown
Reviews triggered through `POST /review` are exempt from the roundtrip limit
(`Naudit:Review:MaxRoundtrips`): the merge gate always gets a fresh verdict, and the endpoint
doubles as the way to force another review once the webhook limit is reached.
```

- [ ] **Step 3: CLAUDE.md**

Im „Request flow“-Absatz nach dem Satz über den Verdict-Gate-Mechanismus ergänzen:

```markdown
Webhook-triggered reviews are throttled by a roundtrip limit (`Naudit:Review:MaxRoundtrips`,
default `3`, `0` = unlimited): `ReviewService` skips early (before any platform call/checkout/LLM)
once that many reviews were already posted for the MR/PR, counted from the existing `ReviewEntity`
audit rows via `IReviewRoundtripCounter` (Core seam, EF impl — fail-open on counter errors). The
last allowed review appends a notice line to its summary. `POST /review` (CI) sets
`ReviewRequest.Trigger = Ci` and is never limited. GitLab `update` webhook events are additionally
filtered on `object_attributes.oldrev` (only real pushes carry it), so label/description edits and
comments never trigger reviews on either platform.
```

- [ ] **Step 4: Commit**

```bash
git add docs/configuration.md docs/ci-integration.md CLAUDE.md
git commit -m "docs: Roundtrip-Limit + Nur-bei-Push-Filter dokumentieren"
```

---

## Abschluss

Nach Task 6: Full Suite + Frontend noch einmal komplett (`dotnet test Naudit.slnx`, `npm run lint && npm run build` in `src/frontend`), Branch `feat/review-roundtrip-limit` pushen, PR gegen `main` (Benedikt merged selbst nach CI/CodeRabbit). Falls PR #53 zuerst gemergt wurde: vor dem PR `main` reinmergen/rebasen (Konfliktstellen: `ReviewRequest`-Signatur, `ReviewService`-Konstruktor, `ReviewServiceTests.CreateService`, `/review`-Endpoint — `AuthorLogin` und `Trigger` koexistieren als Parameter 4 und 5).
