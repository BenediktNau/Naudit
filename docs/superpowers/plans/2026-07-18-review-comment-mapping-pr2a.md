# Review Memory PR 2a (comment→finding mapping) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist, per posted inline finding, the platform id(s) of the comment Naudit created — GitHub review-comment id, GitLab discussion id **and** note id — so a later reply on that comment can be mapped back to the finding. Foundation for PR 2b (`@naudit fp` reply command) and the review-analytics feature (spec `2026-07-17-review-analytics-design.md`).

**Architecture:** `IGitPlatform.PostReviewAsync` (the only Core seam that crosses into the platform clients) changes from `Task` to `Task<IReadOnlyList<PostedComment>>`, returning one `PostedComment` per input inline comment, aligned by index. `ReviewService` zips those ids onto the audit findings; `EfReviewAuditSink` persists them on two new nullable columns of `ReviewFindingEntity`. GitLab captures the ids directly from its `POST …/discussions` response; GitHub captures them via a follow-up `GET …/reviews/{id}/comments` matched by path+line. No webhook or command handling yet — that is PR 2b.

**Tech Stack:** .NET 10, EF Core (SQLite/Postgres), MEAI abstractions, xUnit, `StubHttpMessageHandler` for the HTTP clients.

## Global Constraints

- Build/test **only** via `Naudit.slnx` — `Naudit.sln` does not exist.
- **Core rule:** `Naudit.Core` references only `Microsoft.Extensions.AI.Abstractions`. `PostedComment` is a Core model (`Naudit.Core.Models`); no SDK/HTTP types cross into Core.
- Code comments in **German**; docs (`docs/**`, README) in **English**.
- Migrations are **hand-kept provider-neutral**: `AddColumn` with no explicit type; no `HasColumnType` in the new `.Designer.cs`; the model snapshot stays SQLite-baked (repo convention — see the existing `20260715120000_AddSharePoolFlag` migration as the template for a column-add).
- The change must be **behavior-preserving for the review itself**: capturing ids is best-effort; a capture failure must never fail or change the posted review. Return `null` ids rather than throwing.
- TDD: failing test first, watch it fail, minimal implementation, watch it pass. One commit per task.
- Branch: `feat/review-comment-mapping` (stacked on `feat/review-memory` / PR #61, which is not yet merged — rebase onto `main` once #61 merges).
- Full-suite runs may show environmental inotify flakes; single-class filters are authoritative for red/green, the full suite must pass in Task 5.
- Baseline: full suite is green at **461/461** at branch start.

---

### Task 1: DB columns `PlatformCommentId` + `PlatformNoteId` on `ReviewFindingEntity`

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs`
- Create: `src/Naudit.Infrastructure/Data/Migrations/<ts>_AddFindingCommentIds.cs` (+ `.Designer.cs`, + snapshot update)
- Test: `tests/Naudit.Tests/DbReviewMemoryTests.cs` (append a roundtrip assertion)

**Interfaces:**
- Produces: `ReviewFindingEntity.PlatformCommentId` (`string?`), `ReviewFindingEntity.PlatformNoteId` (`string?`).

- [ ] **Step 1: Write the failing test** — append to `DbReviewMemoryTests.cs` (reuses its `NewMigratedDb`/`SeedProject` helpers):

```csharp
[Fact]
public async Task ReviewFinding_persistsPlatformCommentAndNoteIds_afterMigrate()
{
    await using var db = NewMigratedDb();
    var project = SeedProject(db);
    var review = new ReviewEntity
    {
        ProjectId = project.Id, PrNumber = 1, Title = "T", Verdict = "approve", Summary = "S",
        CreatedAt = DateTime.UtcNow,
        Findings = { new ReviewFindingEntity
        {
            Severity = "High", Confidence = "High", File = "a.cs", Line = 1, Text = "f",
            PlatformCommentId = "gh-12345", PlatformNoteId = "gl-note-678",
        } },
    };
    db.Reviews.Add(review);
    await db.SaveChangesAsync();

    var loaded = await db.ReviewFindings.SingleAsync();
    Assert.Equal("gh-12345", loaded.PlatformCommentId);
    Assert.Equal("gl-note-678", loaded.PlatformNoteId);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DbReviewMemoryTests`
Expected: FAIL — compile error (`PlatformCommentId` not defined).

- [ ] **Step 3: Add the properties** — in `Entities.cs`, extend `ReviewFindingEntity` (after `Text`):

```csharp
    /// <summary>Plattform-Id des von Naudit geposteten Inline-Kommentars — GitHub: Review-Comment-Id;
    /// GitLab: Discussion-Id. Anker, um eine Antwort auf den Kommentar diesem Finding zuzuordnen
    /// (PR 2b + Auswertung). Null bei Findings ohne Position oder wenn die Erfassung fehlschlug.</summary>
    public string? PlatformCommentId { get; set; }

    /// <summary>GitLab-Note-Id des Discussion-Wurzelkommentars (zusätzlich zur Discussion-Id in
    /// PlatformCommentId) — Award-Emoji-Events referenzieren die Note, nicht die Discussion.
    /// Auf GitHub null.</summary>
    public string? PlatformNoteId { get; set; }
```

- [ ] **Step 4: Add the migration** — `export PATH="$PATH:$HOME/.dotnet/tools"` then `dotnet ef migrations add AddFindingCommentIds --project src/Naudit.Infrastructure`. Hand-neutralize the generated `Up()` to (drop any `type:`):

```csharp
            migrationBuilder.AddColumn<string>(name: "PlatformCommentId", table: "ReviewFindings", nullable: true);
            migrationBuilder.AddColumn<string>(name: "PlatformNoteId", table: "ReviewFindings", nullable: true);
```

`Down()` = two `migrationBuilder.DropColumn(...)`. Add the file-level comment `// Wie AddSharePoolFlag bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).`. In the generated `*_AddFindingCommentIds.Designer.cs`, delete every `.HasColumnType(...)` call (repo convention — the whole Designer is neutralized, matching the existing `*.Designer.cs` files). Leave `NauditDbContextModelSnapshot.cs` SQLite-baked as generated.

- [ ] **Step 5: Run to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DbReviewMemoryTests|NauditDbContextTests"`
Expected: PASS (existing migration tests stay green).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Data/ tests/Naudit.Tests/DbReviewMemoryTests.cs
git commit -m "feat(memory): PlatformCommentId + PlatformNoteId am ReviewFinding + Migration AddFindingCommentIds"
```

---

### Task 2: Core seam — `PostedComment` + `PostReviewAsync` return + audit persistence

**Files:**
- Create: `src/Naudit.Core/Models/PostedComment.cs`
- Modify: `src/Naudit.Core/Abstractions/IGitPlatform.cs`
- Modify: `src/Naudit.Core/Models/ReviewAudit.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `src/Naudit.Infrastructure/Ui/EfReviewAuditSink.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`, `tests/Naudit.Tests/EfReviewAuditSinkTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `record PostedComment(string? CommentId, string? NoteId)`; `IGitPlatform.PostReviewAsync(...) : Task<IReadOnlyList<PostedComment>>` (one entry per input inline comment, index-aligned); `AuditFinding` gains trailing `string? PlatformCommentId = null, string? PlatformNoteId = null`.

- [ ] **Step 1: Write the failing tests** — append to `ReviewServiceTests.cs`. First extend `FakeGitPlatform` to return configurable ids and expose them; a helper on the fake:

```csharp
[Fact]
public async Task ReviewAsync_persistsPostedCommentIds_ontoAuditFindings()
{
    var chat = new FakeChatClient("""{"summary":"ok","comments":[{"file":"a.cs","line":1,"comment":"bug","severity":"high","confidence":"high"}]}""");
    var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")])
    {
        PostedIds = [new PostedComment("gh-1", "gl-9")],   // aligned to the one inline comment
    };
    var audit = new FakeReviewAuditSink();
    var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, auditSink: audit);

    await service.ReviewAsync(Request);

    var finding = Assert.Single(audit.LastAudit!.Findings);
    Assert.Equal("gh-1", finding.PlatformCommentId);
    Assert.Equal("gl-9", finding.PlatformNoteId);
}
```

(If `FakeReviewAuditSink` does not already expose `LastAudit`, add `public ReviewAudit? LastAudit { get; private set; }` set in its `RecordAsync`.)

Append to `EfReviewAuditSinkTests.cs` (uses its existing DB/seed idiom):

```csharp
[Fact]
public async Task RecordAsync_persistsPlatformCommentAndNoteIds()
{
    using var test = new TestDb();
    var sink = new EfReviewAuditSink(test.Context, NullLogger<EfReviewAuditSink>.Instance);
    var audit = new ReviewAudit("owner/repo", 1, "T", ReviewVerdict.Approve, "S",
        [new AuditFinding(FindingSeverity.High, ReviewConfidence.High, "a.cs", 1, "f", "gh-1", "gl-9")],
        null, null, null);

    await sink.RecordAsync(audit);

    var f = await test.Context.ReviewFindings.SingleAsync();
    Assert.Equal("gh-1", f.PlatformCommentId);
    Assert.Equal("gl-9", f.PlatformNoteId);
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ReviewServiceTests|EfReviewAuditSinkTests"`
Expected: FAIL — compile errors (`PostedComment`, `PostedIds`, extra `AuditFinding` args not defined).

- [ ] **Step 3: Implement.** Create `src/Naudit.Core/Models/PostedComment.cs`:

```csharp
namespace Naudit.Core.Models;

/// <summary>Plattform-Ids eines geposteten Inline-Kommentars, index-gleich zur Eingabe-Kommentarliste.
/// CommentId: GitHub-Review-Comment-Id bzw. GitLab-Discussion-Id. NoteId: GitLab-Note-Id (GitHub null).
/// Beide null möglich, wenn die Plattform keine Id lieferte oder die Erfassung fehlschlug.</summary>
public sealed record PostedComment(string? CommentId, string? NoteId);
```

In `IGitPlatform.cs` change the `PostReviewAsync` return type and doc:

```csharp
    /// <summary>Postet Summary + Inline-Kommentare. Liefert je Eingabe-Inline-Kommentar (index-gleich)
    /// die Plattform-Ids des erzeugten Kommentars zurück (für die spätere Antwort-Zuordnung).
    /// Erfassung ist best-effort — schlägt sie fehl, kommen null-Ids, nie eine Exception.</summary>
    Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default);
```

In `ReviewAudit.cs` extend `AuditFinding`:

```csharp
public sealed record AuditFinding(
    FindingSeverity Severity, ReviewConfidence Confidence, string? File, int? Line, string Text,
    string? PlatformCommentId = null, string? PlatformNoteId = null);
```

In `ReviewService.cs`: capture the return and thread it into the audit. Change the post call:

```csharp
        var posted = await gitPlatform.PostReviewAsync(request, summary, inline, verdict, ct);
        await RecordAuditAsync(request, verdict, summary, inline, orphans, posted, response, selection.UsedSessionAccountId(), ct);
```

Update `RecordAuditAsync`'s signature to take `IReadOnlyList<PostedComment> posted` (after `orphans`) and set the ids on inline audit findings by index:

```csharp
            var findings = new List<AuditFinding>(inline.Count + orphans.Count);
            for (var i = 0; i < inline.Count; i++)
            {
                var p = i < posted.Count ? posted[i] : null;
                var it = inline[i];
                findings.Add(new AuditFinding(it.Severity, it.Confidence, it.FilePath, it.NewLine, it.Body,
                    p?.CommentId, p?.NoteId));
            }
            foreach (var o in orphans)
                findings.Add(new AuditFinding(o.Severity, o.Confidence, o.File, null, o.Body));
```

In `EfReviewAuditSink.cs` add the two fields to the `ReviewFindingEntity` init:

```csharp
                Text = f.Text,
                PlatformCommentId = f.PlatformCommentId,
                PlatformNoteId = f.PlatformNoteId,
```

In `FakeGitPlatform.cs` add a settable `PostedIds` and return it (default: aligned nulls):

```csharp
    public IReadOnlyList<PostedComment> PostedIds { get; set; } = [];

    public Task<IReadOnlyList<PostedComment>> PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
    {
        PostedMarkdown = summaryMarkdown;
        PostedComments = comments;
        PostedVerdict = verdict;
        PostCallCount++;
        // Standard: je Kommentar ein leerer PostedComment, sofern der Test nichts vorgibt.
        IReadOnlyList<PostedComment> result = PostedIds.Count > 0
            ? PostedIds
            : comments.Select(_ => new PostedComment(null, null)).ToList();
        return Task.FromResult(result);
    }
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ReviewServiceTests|EfReviewAuditSinkTests"`
Expected: PASS. Then `dotnet build Naudit.slnx` — it WILL fail to compile until Tasks 3 & 4 update `GitLabPlatform`/`GitHubPlatform` to the new return type. That is expected; those two classes are the only remaining `IGitPlatform` implementors. If you want a green build at this task boundary, apply the minimal `return [];` stub in both platform classes now and flesh them out in Tasks 3/4 — otherwise proceed and let Task 3/4 close the build. State which you did in the report.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/ src/Naudit.Infrastructure/Ui/EfReviewAuditSink.cs tests/Naudit.Tests/Fakes/FakeGitPlatform.cs tests/Naudit.Tests/ReviewServiceTests.cs tests/Naudit.Tests/EfReviewAuditSinkTests.cs
git commit -m "feat(memory): PostReviewAsync liefert PostedComment-Ids, Audit persistiert sie am Finding"
```

---

### Task 3: GitLabPlatform — capture discussion id + note id

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (add a discussion-response DTO)
- Test: `tests/Naudit.Tests/GitLabPlatformTests.cs`

**Interfaces:**
- Consumes: `PostedComment` (Task 2).
- Produces: `GitLabPlatform.PostReviewAsync` returns `IReadOnlyList<PostedComment>` — one per inline comment, `CommentId` = discussion id, `NoteId` = the discussion's root note id.

- [ ] **Step 1: Write the failing test** — in `GitLabPlatformTests.cs`, add a test that posts one inline comment and asserts the returned `PostedComment` carries the discussion id + note id parsed from the `POST …/discussions` response. Follow the file's existing `StubHttpMessageHandler` idiom (it already asserts URL + body for the summary note and discussion). The stub must return, for the discussions POST, a JSON body like:

```json
{ "id": "disc-abc", "notes": [ { "id": 555 } ] }
```

and the test asserts:

```csharp
var posted = await platform.PostReviewAsync(request, "sum", [inlineComment], ReviewVerdict.Approve);
var pc = Assert.Single(posted);
Assert.Equal("disc-abc", pc.CommentId);
Assert.Equal("555", pc.NoteId);
```

(Model the request/inlineComment exactly on the existing GitLab inline-comment test in the same file; reuse its `diff_refs` stub for the MR GET.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabPlatformTests`
Expected: FAIL — either compile (return type) or assertion (ids not captured).

- [ ] **Step 3: Implement.** In `GitLabDtos.cs` add:

```csharp
/// <summary>Antwort von POST …/discussions — Discussion-Id + Wurzel-Note-Id (für die Antwort-Zuordnung).</summary>
public sealed record GitLabDiscussionResponse(string? Id, List<GitLabDiscussionNote>? Notes);
public sealed record GitLabDiscussionNote(long Id);
```

In `GitLabPlatform.cs` change `PostReviewAsync` to collect one `PostedComment` per inline comment. Replace the discussion-post loop body so it parses the response:

```csharp
        var posted = new List<PostedComment>(comments.Count);
        // ... innerhalb der bestehenden Inline-Schleife, statt nur EnsureSuccessStatusCode():
        using var discResp = await SendAsync(HttpMethod.Post, $"{basePath}/discussions", request.ProjectId, payload, ct);
        discResp.EnsureSuccessStatusCode();
        var disc = await discResp.Content.ReadFromJsonAsync<GitLabDiscussionResponse>(ct);
        posted.Add(new PostedComment(disc?.Id, disc?.Notes is { Count: > 0 } n ? n[0].Id.ToString() : null));
```

Ensure every inline comment appends exactly one `PostedComment` (even on a non-2xx you would have thrown; that is fine — a throw aborts the whole post as today). Change the method signature to `async Task<IReadOnlyList<PostedComment>>` and `return posted;` at the end. The summary note and the approve/unapprove calls are unchanged. If an inline comment is skipped for any reason, it must still append a `new PostedComment(null, null)` to keep index alignment — but the current code posts every passed comment, so a 1:1 append is correct.

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabPlatformTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitLab/ tests/Naudit.Tests/GitLabPlatformTests.cs
git commit -m "feat(memory): GitLabPlatform erfasst Discussion-Id + Note-Id je Inline-Kommentar"
```

---

### Task 4: GitHubPlatform — capture review-comment ids

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs` (review-id + review-comment DTOs)
- Test: `tests/Naudit.Tests/GitHubPlatformTests.cs`

**Interfaces:**
- Consumes: `PostedComment` (Task 2).
- Produces: `GitHubPlatform.PostReviewAsync` returns `IReadOnlyList<PostedComment>` — `CommentId` = the review-comment id matched by (path, line); `NoteId` = null.

- [ ] **Step 1: Write the failing test** — in `GitHubPlatformTests.cs`, add a test where the stub returns, for `POST …/reviews`, a body `{ "id": 4242 }`, and for the follow-up `GET …/reviews/4242/comments`, an array `[ { "id": 77, "path": "a.cs", "line": 1 } ]`. Assert:

```csharp
var posted = await platform.PostReviewAsync(request, "sum", [inlineOnA_cs_line1], ReviewVerdict.Approve);
Assert.Equal("77", Assert.Single(posted).CommentId);
Assert.Null(posted[0].NoteId);
```

Model the request/inline comment on the existing GitHub inline test in the same file. The stub must handle: the reviews POST (returns `{id:4242}`) and the reviews-comments GET (returns the array). (The existing `GetChanges`/repo stubs are not exercised on this path.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubPlatformTests`
Expected: FAIL.

- [ ] **Step 3: Implement.** In `GitHubDtos.cs` add:

```csharp
/// <summary>Antwort von POST …/reviews — die Review-Id, um danach genau die Kommentare DIESES Reviews zu holen.</summary>
public sealed record GitHubReviewResponse(long Id);
/// <summary>Ein Review-Comment aus GET …/reviews/{id}/comments — Id + Position, zum Matchen an unsere Inline-Kommentare.</summary>
public sealed record GitHubReviewComment(long Id, string? Path, int? Line);
```

In `GitHubPlatform.cs`, after the review is successfully posted (both the normal and the 422→COMMENT fallback path), read the review id from the response body, GET its comments, and match each input inline comment by `Path == FilePath && Line == NewLine`:

```csharp
    // Nach erfolgreichem Post: Review-Id lesen, dessen Kommentare holen und je Inline-Kommentar
    // per (Pfad, Zeile) die Review-Comment-Id zuordnen. Best-effort: jeder Fehler ⇒ null-Ids,
    // der Review ist bereits gepostet und darf nicht mehr kippen.
    private async Task<IReadOnlyList<PostedComment>> CaptureCommentIdsAsync(
        ReviewRequest request, HttpResponseMessage reviewResponse,
        IReadOnlyList<InlineComment> comments, CancellationToken ct)
    {
        var fallback = comments.Select(_ => new PostedComment(null, null)).ToList();
        try
        {
            var review = await reviewResponse.Content.ReadFromJsonAsync<GitHubReviewResponse>(ct);
            if (review is null) return fallback;
            using var resp = await SendAsync(HttpMethod.Get,
                $"repos/{request.ProjectId}/pulls/{request.MergeRequestIid}/reviews/{review.Id}/comments?per_page=100",
                request.ProjectId, null, ct);
            resp.EnsureSuccessStatusCode();
            var posted = await resp.Content.ReadFromJsonAsync<List<GitHubReviewComment>>(ct) ?? [];
            return comments.Select(c =>
            {
                var m = posted.FirstOrDefault(p => p.Path == c.FilePath && p.Line == c.NewLine);
                return new PostedComment(m?.Id.ToString(), null);
            }).ToList();
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return fallback;   // Erfassung best-effort — der Review steht bereits.
        }
    }
```

`PostReviewAsync` returns `IReadOnlyList<PostedComment>`: keep the `response`/`fallback` handling, and at the end call `return await CaptureCommentIdsAsync(request, <the successful response>, comments, ct);`. Note `PostReviewOnceAsync` currently `using`-disposes its response — restructure so the successful response object is still readable when passed to `CaptureCommentIdsAsync` (read its body before disposing, or don't `using`-dispose until after capture). Do NOT capture on an empty `comments` list (return `[]` early).

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitHubPlatformTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Git/GitHub/ tests/Naudit.Tests/GitHubPlatformTests.cs
git commit -m "feat(memory): GitHubPlatform erfasst Review-Comment-Id je Inline-Kommentar (Match per Pfad+Zeile)"
```

---

### Task 5: Docs + full verification

**Files:**
- Modify: `docs/review-memory.md` (a "Comment mapping (foundation for the reply command)" section)
- Modify: `CLAUDE.md` (extend the review-memory extension-point bullet)

- [ ] **Step 1: Update `docs/review-memory.md`** — add a short section explaining that each posted inline finding now stores its platform comment id(s) (`ReviewFindingEntity.PlatformCommentId` = GitHub review-comment id / GitLab discussion id; `PlatformNoteId` = GitLab note id, GitHub null), that capture is best-effort (null on failure, never fails the review), and that this is the anchor PR 2b's `@naudit fp` reply command and the analytics feature use. Replace the earlier "outlook" wording that said the note id was still a forward requirement — it is now implemented.

- [ ] **Step 2: Update `CLAUDE.md`** — in the request-flow / review-memory area, note that `IGitPlatform.PostReviewAsync` returns `IReadOnlyList<PostedComment>` (Core-visible), whose ids the audit sink persists on `ReviewFindingEntity.PlatformCommentId`/`PlatformNoteId` for later comment→finding mapping.

- [ ] **Step 3: Full verification**

```bash
dotnet test Naudit.slnx
```

Expected: full suite green (report the count; must be ≥ the 461 baseline plus the new tests). No frontend change in this PR.

- [ ] **Step 4: Commit**

```bash
git add docs/review-memory.md CLAUDE.md
git commit -m "docs(memory): Comment→Finding-Mapping (PostedComment-Ids) dokumentiert"
```

## Self-review checklist (run after writing all tasks)

- Every `IGitPlatform` implementor (`GitLabPlatform`, `GitHubPlatform`, `FakeGitPlatform`) returns the new type — no build break left open past Task 4.
- Index alignment: each platform appends exactly one `PostedComment` per input inline comment; orphan (positionless) findings are never in the `comments` list, so they correctly get no id.
- Capture is best-effort everywhere: a failed GET (GitHub) or a missing field (GitLab) yields null ids, never an exception that aborts an already-posted review.
- Migration is provider-neutral (no `type:`, Designer HasColumnType-free, snapshot left baked).
