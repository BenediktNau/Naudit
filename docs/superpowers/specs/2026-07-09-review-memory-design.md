# Review memory (false positives & project conventions) — Design

*2026-07-09 · Naudit*

## Problem

Naudit has no memory. When the LLM flags something a maintainer considers wrong — a false
positive — the bot will happily flag the same thing again on the next MR/PR touching that code.
There is also no way to teach the bot project-specific conventions ("we intentionally use X",
"Y is acceptable here"), so the same discussions repeat. Both erode trust in the bot faster than
any missed finding does.

**Non-goals:** a general knowledge base, cross-project/global rules, and automatic learning from
accepted findings. The memory is explicit, human-curated, and per project.

## Decisions (settled during brainstorming, 2026-07-09)

1. **Scope of content:** false positives **and** free-text project conventions. Nothing else.
2. **Feedback channels: both** — the WebUI (base, PR 1) and a reply command on the bot's inline
   PR/MR comment (PR 2).
3. **Mechanism: prompt guidance.** Memory entries are injected into the prompt as a read-only
   guidance section ("known false positives & project conventions — do not report these again").
   No post-LLM filtering; the verdict stays LLM-driven through the existing severity gate.
4. **Deterministic selection now, RAG-ready seam.** Selection is a Core interface
   (`IReviewMemory`) that deliberately receives the full `CodeChange` list (not just file paths),
   so a later embedding-based selector ("RAG light": embeddings as blobs in the same table,
   in-memory cosine — no vector DB) is a drop-in second implementation. Rationale: a per-project
   memory stays small (tens of entries); embeddings would add a hard dependency on an
   embedding-capable provider (Anthropic and the Claude-Code-CLI provider have none) and
   silent-miss failure modes, for a scale problem Naudit does not have yet.
5. **Project-scoped only.** Every entry belongs to exactly one project. Instance-wide
   conventions are a possible later addition, not part of this design.

## Architecture

Same seam pattern as `IPromptRedactor` / `IContextCollector`: interface + models in Core,
implementation in Infrastructure, selection/config in `AddNauditInfrastructure`. Core keeps
depending on MEAI abstractions only.

### Core additions

```csharp
// Naudit.Core.Abstractions
/// <summary>Liefert die für dieses Review relevanten Gedächtnis-Einträge (FPs + Konventionen).</summary>
public interface IReviewMemory
{
    // Bekommt bewusst die CodeChanges (nicht nur Pfade), damit ein späterer
    // Embedding-Selector dieselbe Signatur nutzen kann.
    Task<IReadOnlyList<MemoryEntry>> SelectAsync(
        string projectId, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}

// Naudit.Core.Models
public enum MemoryKind { FalsePositive, Convention }

/// <summary>Ein Gedächtnis-Eintrag: als falsch markierter Fund oder Projekt-Konvention.</summary>
public sealed record MemoryEntry(MemoryKind Kind, string? File, string Text, string? Reason);
```

`ReviewOptions` gains a `Memory` sub-options class (bound from `Naudit:Review:Memory`, same
mechanism as `Gate` / `Context`):

```csharp
public sealed class ReviewMemoryOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxEntries { get; set; } = 50;   // Deckel für die Prompt-Sektion
}
```

### Infrastructure additions

**Entity** (`MemoryEntryEntity` in `Data/Entities.cs`; migration hand-neutralized for
SQLite + Postgres like the existing ones):

| Column | Notes |
|---|---|
| `Id` | PK |
| `ProjectId` | FK → `ProjectEntity` |
| `Kind` | `"FalsePositive"` \| `"Convention"` (string, like `Severity`/`Verdict`) |
| `File` | nullable — conventions and file-less FPs |
| `Text` | the finding text (FP) or the convention sentence |
| `Reason` | nullable — optional "why is this an FP" note from the marker |
| `SourceFindingId` | nullable FK → `ReviewFindingEntity` (`SetNull` on delete); set when created from a finding, unique among non-null values (idempotency anchor) |
| `CreatedBy` | WebUI username or platform login |
| `CreatedAt` | UTC |
| `Active` | bool — deactivate instead of delete, audit trail stays |

**`DbReviewMemory`** (`src/Naudit.Infrastructure/Memory/DbReviewMemory.cs`) — the deterministic
default implementation:

- loads the project's **active** entries (`ProjectEntity` looked up by `PlatformProjectId`;
  unknown project ⇒ empty list);
- selects **all conventions** (their `File` is display-only scoping for the LLM, never a
  selection filter) + FPs whose `File` matches a `FilePath` in the diff (exact match) +
  file-less FPs;
- caps at `MaxEntries` — conventions first, then FPs; newest-first within each group;
- **fail-open:** any exception is caught inside the implementation, logged, and returns an
  empty list — a memory hiccup never fails the review (audit-sink philosophy).

`Naudit:Review:Memory:Enabled=false` registers `NullReviewMemory` (no-op, empty list) instead —
exactly the redaction pattern. Both new config keys join the `SettingsCatalog` whitelist
(non-secret) so they are editable from the Settings page.

## Request-flow changes (`ReviewService`)

`ReviewService` gains an `IReviewMemory` constructor dependency and calls `SelectAsync` after
`GetChangesAsync` (independent of the workspace checkout — memory needs no repo). Entry `Text`
and `Reason` pass through `IPromptRedactor` like diffs/findings/title: nothing reaches the LLM
unredacted. The redacted entries go into `PromptBuilder.Build`.

## Prompt changes (`PromptBuilder`)

`Build` gains an optional `IReadOnlyList<MemoryEntry>? memory = null` parameter. Rendering order:
title → diffs → context → findings → **memory section last** (closest to the response, maximum
instruction weight). Empty memory renders nothing — prompt is byte-identical to today.

```text
# Project memory (maintainer guidance)

## Known false positives — do NOT report these or equivalent findings again
- src/Foo/Bar.cs: <finding text> (maintainer note: <reason>)
- <file-less entry text>

## Project conventions — respect these when judging the diff
- <convention text>
- src/Legacy/: <convention text scoped to a file>
```

System-prompt addition (appended to `DefaultSystemPrompt`): a "Project memory" section may
follow — it contains maintainer decisions; do not report findings matching a known false
positive, and treat the conventions as authoritative project rules, not as code smells to flag.

## Feedback channel 1: WebUI (PR 1)

- **Finding view:** every finding in the review detail gets a **"False positive"** action →
  `POST /api/findings/{id}/false-positive` with optional body `{ "reason": "..." }`. Creates the
  `MemoryEntry` (Kind=FalsePositive, `File`/`Text` copied from the finding, `SourceFindingId`
  set, `CreatedBy` = session user). Idempotent: an existing entry for the same finding is
  reactivated/updated, not duplicated. `DELETE` on the same route deactivates (mis-click undo).
- **Memory page per project:** lists all entries (incl. inactive), create conventions
  (`POST /api/projects/{id}/memory`, body `{ "text": "...", "file": null }`), toggle
  `PUT /api/memory/{id}` `{ "active": bool }`.
- **Authorization:** same project visibility rule as the dashboard (owning account or admin);
  401/403 handling as in the existing JSON API.

## Feedback channel 2: PR/MR reply command (PR 2)

- **Command:** reply to the bot's inline comment with `@naudit fp <optional reason>`
  (`fp` | `false-positive`, case-insensitive; rest of the line = reason).
- **Comment→finding mapping:** `IGitPlatform.PostReviewAsync` returns the platform IDs of the
  posted inline comments (aligned with the passed comment list); the audit sink stores them as
  `PlatformCommentId` on `ReviewFindingEntity` (GitHub: review-comment id, matched via the
  reply's `in_reply_to_id`; GitLab: discussion id, matched via the reply note's discussion).
  This is a small Core-visible signature change on the existing `IGitPlatform` seam.
- **Webhook events on the existing endpoints:** GitHub `pull_request_review_comment`
  (action `created`) via `X-Hub-Signature-256` as today; GitLab Note Hook
  (`object_kind: note`, noteable `MergeRequest`) via the secret token as today. The bot's own
  comments and non-command replies are ignored. Handled synchronously in the endpoint (one DB
  write, GitLab plus one membership API call), always answering 200 after signature validation.
- **Authorization, fail-closed:** GitHub `author_association` ∈ {OWNER, MEMBER, COLLABORATOR};
  GitLab membership lookup (`/members/all/{user_id}`) requiring access level ≥ Developer.
  Unverifiable ⇒ ignore (log, 200).
- **Confirmation:** the bot replies in the same thread ("Als False Positive gemerkt." — German,
  matching the existing bot texts).
- **Setup wizard follow-up:** GitLab hook creation adds `note_events: true`; the GitHub App
  manifest subscribes to `pull_request_review_comment`.

## Error handling & security assumptions

- Memory selection failure ⇒ fail-open (empty memory, logged), review proceeds.
- Memory entries are user-authored text that lands in the prompt. Prompt injection through
  malicious entries is an **accepted risk**, mitigated by authorization: only repo members
  (platform channel) or visible-project accounts (WebUI) can write entries, and everything is
  attributed (`CreatedBy`) and auditable (`Active` instead of delete).
- Redaction applies to memory content like to every other prompt ingredient.

## Testing

- **`ReviewService`** (new `FakeReviewMemory`): entries appear in the prompt and pass the
  redactor; empty memory ⇒ prompt unchanged; memory fake throwing is not a concern here
  (fail-open lives in `DbReviewMemory`).
- **`PromptBuilder`:** renders the section (FP with/without reason, convention, file-scoped);
  empty list/null ⇒ byte-identical prompt.
- **`DbReviewMemory`** against SQLite in-memory: file match, file-less FPs, conventions always
  included, cap order (conventions first, FPs newest-first), inactive excluded, unknown project
  ⇒ empty, DB exception ⇒ empty + log.
- **API endpoints:** mark/undo FP (idempotency, auth, 401/403), conventions CRUD, toggle.
- **PR 2:** command parsing (both platforms, bot-self filter, reason extraction), authorization
  matrix (fail-closed), comment-ID mapping unit tests with `StubHttpMessageHandler`; 401
  signature path via `WebApplicationFactory` (existing pattern).

## PR slicing

1. **PR 1 — memory core + WebUI:** seam, entity/migration, `DbReviewMemory`, prompt section,
   `ReviewService` wiring, WebUI (FP action, memory page), config keys + `SettingsCatalog`,
   `docs/review-memory.md`, CLAUDE.md extension point.
2. **PR 2 — platform command:** `PostReviewAsync` return change + `PlatformCommentId`
   persistence, comment webhook handling for both platforms, authorization, confirmation reply,
   wizard hook/manifest update.

## Out of scope (future, same seam)

- **`EmbeddingReviewMemory` ("RAG light"):** embed entries on write (MEAI
  `IEmbeddingGenerator`), store vectors as blobs in the same table, cosine-match against the
  embedded diff in memory. Needs an embedding-capable provider and a model-versioning column;
  no vector DB required at this scale.
- Instance-wide (global) conventions.
- Glob/path-prefix scoping for conventions.
- Auto-learning from accepted or repeatedly-posted findings.
