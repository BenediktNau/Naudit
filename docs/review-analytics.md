# Review analytics (finding resolution tracking & acceptance dashboard)

Naudit posts findings but, on its own, never learns what happened to them. Review
analytics closes that loop: every posted finding can carry a **resolution
status** — accepted, rejected, or still unanswered — captured from how people
actually react to it in the PR/MR or in the WebUI, and rolled up into a small
per-project dashboard.

This is **PR 3** of the review-analytics feature (see the design doc,
`docs/superpowers/specs/2026-07-17-review-analytics-design.md`): explicit
signals only. LLM-classified free-text replies, the GitHub checkbox footer,
and GitLab award-emoji events are **PR 4** — see [Outlook](#outlook-pr-4)
below.

## What it tracks

`ReviewFindingEntity` (migration `AddResolutionTracking`) gained four nullable
columns:

| Column | Meaning |
| --- | --- |
| `ResolutionStatus` | `"Accepted"` \| `"Rejected"` — `null` = unanswered |
| `ResolutionSource` | who/what last set the status: `"Command"` (`@naudit ok`/`fp`) \| `"WebUi"` (dashboard buttons) \| `"Llm"` (PR 4) — `"Checkbox"`/`"Emoji"` are reserved for PR 4 |
| `ResolvedBy` | platform login (command) or WebUI username |
| `ResolvedAtUtc` | UTC timestamp of the last resolution write |

The same migration adds `MemoryEntryEntity.TimesApplied` (int, default `0`)
and `.LastAppliedAtUtc` (nullable) — see
[`TimesApplied` — memory impact](#timesapplied--memory-impact) below. Nothing
here changes the merge verdict: resolution is purely an after-the-fact signal,
recorded once a review is already posted.

## Signals shipped in PR 3

Both signals write through the same helper, `ResolutionWriter.ApplyAsync(db,
finding, status, source, by)`, which enforces the precedence rule below and
returns whether anything actually changed (so callers can skip a redundant
confirmation reply on webhook redelivery).

### `@naudit ok` / `@naudit fp` reply commands

Replying to one of Naudit's inline comments reuses the comment→finding
mapping and webhook plumbing from the memory feature (`PlatformCommentId`,
GitHub `pull_request_review_comment`/`created`, GitLab Note Hook, the same
fail-closed author check) — see [Review memory](review-memory.md#reply-command-naudit-fp-pr-2b).
The command parser (`FpReplyCommand`) now recognizes two verbs, case-insensitive,
at the start of the first line:

- `@naudit fp` / `@naudit false-positive <reason>` → `ReviewCommandKind.FalsePositive`
- `@naudit ok` / `@naudit angenommen` / `@naudit accepted <text>` → `ReviewCommandKind.Accept`

`ReviewCommentCommandService` handles both, always answering the webhook `200`:

- **`fp`** creates/reactivates the false-positive memory entry (unchanged from
  PR 2b) **and**, only if `Naudit:Review:Resolution:Enabled` is `true`, also
  writes `Rejected`/`Command` via `ResolutionWriter` — one command feeds both
  the memory and the statistics. With `Enabled=false` the memory entry is
  still created; only the resolution write is skipped.
- **`ok`** writes `Accepted`/`Command`. If `Enabled=false` the command is
  ignored entirely (logged, no memory counterpart exists for "ok").

On an actual state change, Naudit replies in the same thread — `"Als False
Positive gemerkt."` for `fp`, `"Als angenommen vermerkt."` for `ok` — exactly
once per real transition (redelivery of an already-applied command produces
no second reply).

### WebUI Accept/Reject buttons

The review detail page (`ReviewDetail.tsx`) shows an **accept** and a
**reject** button next to every finding, plus the existing **FP** button
(memory, unrelated switch) in the same row — three independent one-click
actions, not chained. Accept/reject are toggles: clicking an already-active
one undoes it (`status: null`); a resolved finding also shows a
`✓ accepted` / `✗ rejected` pill.

`PUT /api/findings/{id}/resolution`, body `{ "status": "Accepted" |
"Rejected" | null }`:

- `401` with no session, `403` if the caller can't see the finding's project
  (same rule as the dashboard), `404` for an unknown finding id, `400` for
  any non-null status other than `Accepted`/`Rejected`.
- On success writes through `ResolutionWriter` with source `"WebUi"` and
  `ResolvedBy` = the session username, and returns
  `{ id, resolutionStatus }`.
- **Not gated by `Naudit:Review:Resolution:Enabled`** — the switch only
  affects the reply-command capture path (above); the WebUI buttons and the
  analytics endpoint work regardless.

### Precedence rule

`ResolutionWriter.ApplyAsync` implements one rule, shared by every current
and future signal source:

1. **Explicit sources always overwrite.** `Command`, `Checkbox` (PR 4),
   `Emoji` (PR 4), and `WebUi` each unconditionally replace whatever status
   (including another explicit source's) is currently set — no guard, no
   "are you sure".
2. **`Llm` only fills blanks** (PR 4, not wired yet — see
   [Outlook](#outlook-pr-4)): a write with source `Llm` is applied only when
   the finding is currently unresolved or was itself last set by `Llm`. It
   can never overwrite a human decision, and any later explicit signal
   corrects it permanently.
3. **Undo clears only its own source.** A write with `status: null` succeeds
   only if the finding's *current* `ResolutionSource` equals the source
   requesting the undo (e.g. a `WebUi` undo can never clear a `Command`
   decision, and vice versa) — otherwise it is a no-op.
4. **No-op detection.** Writing the identical `(status, source)` pair again
   is a no-op (`false`, no `SaveChangesAsync`) — this is what makes the
   command handlers' redelivery guard work. Note that the *same* status via a
   **different**, still-permitted source is **not** a no-op: it still updates
   `ResolutionSource`/`ResolvedBy`/`ResolvedAtUtc` (e.g. an explicit `WebUi`
   accept over a prior `Llm` accept records the human as the resolver).

`Llm` itself is not yet produced by anything in PR 3 — the rule exists now so
PR 4's classifier can be dropped in without touching `ResolutionWriter` again.

## `GET /api/analytics` contract

`GET /api/analytics?projectId=&days=` — same visibility as the dashboard
(`RequireAuthorization`; `401` with no session; an explicit `projectId` the
caller can't see returns `403`; omitted `projectId` aggregates over every
project visible to the caller). Read-only, unaffected by
`Naudit:Review:Resolution:Enabled` — it only reads whatever was captured.

**Params:**

| Param | Meaning |
| --- | --- |
| `projectId` | optional `int`; omitted = aggregate across all visible projects |
| `days` | optional, one of `7`, `30`, `90`; omitted (or `0`) defaults to `30`; any other value → `400` |

The date window is `[today − (days − 1), today]` (UTC, by the review's
`CreatedAt`, not by when it was resolved).

**Response shape:**

```jsonc
{
  "totals": {
    "posted": 3, "accepted": 1, "rejected": 1, "unanswered": 1,
    "acceptanceRate": 0.333, "fpRate": 0.333   // both 0 when posted = 0, never NaN
  },
  "bySeverity": [
    // one entry per severity with posted > 0 — empty severities are omitted, not zero-filled
    { "severity": "high", "posted": 2, "accepted": 1, "rejected": 0 },
    { "severity": "low",  "posted": 1, "accepted": 0, "rejected": 1 }
  ],
  "weekly": [
    // ISO-week buckets (Monday start), ascending, in-memory grouping (no provider-specific SQL)
    { "weekStart": "2026-07-13", "posted": 2, "accepted": 1, "rejected": 0 }
  ],
  "memory": {
    // across the same visible project(s), not date-windowed
    "entries": 2, "active": 1, "timesApplied": 5
  }
}
```

`unanswered` is `posted − accepted − rejected`, so `acceptanceRate` and
`fpRate` do not have to sum to `1` — an unanswered finding counts as neither
accepted nor rejected, on purpose ("unanswered" reflects reality, not an
assumed thumbs-up). `bySeverity` iterates a fixed, case-insensitive severity
list (`critical`, `high`, `medium`, `low`, `info`).

## The "Auswertung" page

A new top-nav page (`AnalyticsPage.tsx`, nav label **"Auswertung"**,
`AppPage = "analytics"`), built entirely from the existing UI kit — no new
frontend dependency:

- A project selector (dashboard's project list plus "All projects") and a
  `7d`/`30d`/`90d` range toggle, both driving `GET /api/analytics`.
- Four `StatTile`s: **Findings posted** (with a weekly sparkline), **Acceptance
  rate**, **False-positive rate**, and **Memory applied** (`timesApplied`,
  with active/total entry counts as the sub-label).
- A **"By severity"** panel — a horizontal bar per severity showing
  accepted-of-posted, or "No findings in this range" when `bySeverity` is
  empty.
- A **"Weekly trend"** panel — a `Sparkline` of posted-per-week, or "Not
  enough data yet" when fewer than two weekly buckets exist.
- If the caller has no reviewed projects at all, the page shows a single
  line ("No reviewed projects yet — analytics need at least one review")
  instead of empty charts.

## `TimesApplied` — memory impact

Every time `DbReviewMemory.SelectAsync` picks a memory entry (convention or
false positive) into a review's prompt, it increments that entry's
`TimesApplied` and stamps `LastAppliedAtUtc` — a best-effort
`SaveChangesAsync` in its own inner `try`/`catch`, so a failed counter update
never discards the already-computed selection (the review still gets its
memory guidance either way). This is the same idea as CodeRabbit's "learnings
applied" metric: it shows whether curated guidance is actually being used,
not just accumulating unread. The analytics endpoint's `memory.timesApplied`
sums this counter across every `MemoryEntryEntity` (active or not) belonging
to the visible project(s) in scope — it is **not** windowed by `days`.

## Configuration

```jsonc
"Naudit": {
  "Review": {
    "Resolution": {
      "Enabled": true,            // wired in PR 3: gates the @naudit ok/fp resolution
                                   // writes only — WebUI accept/reject and the analytics
                                   // endpoint work regardless (see above)
      "LlmClassification": true,  // PR 4 placeholder — option exists, nothing reads it yet
      "RenderCheckbox": true      // PR 4 placeholder — option exists, nothing reads it yet
    }
  }
}
```

All three keys are DB-managed via the Settings page (`SettingsCatalog`),
non-secret, like most other `Naudit:Review:*` keys — see
[Configuration](configuration.md). Only `Enabled` currently has any effect;
`LlmClassification` and `RenderCheckbox` were added now (options class +
catalog entries) so PR 4 needs no further settings-plumbing change, but no
code path reads them yet.

## Outlook: PR 4 (planned)

Per the design doc, PR 4 adds the remaining "convenience" signals on top of
the same `ResolutionWriter`/precedence rule, with no further schema change:

- **LLM classification** of free-text (non-command) replies on a mapped
  finding thread — redacted, sent to the *global* `IChatClient` only (never
  an author session), result applied with source `Llm` (fills blanks only,
  per the precedence rule above); routed through a small bounded
  queue/`BackgroundService` so a slow/failed classification never blocks or
  fails the webhook.
- **GitHub checkbox** — a rendered `- [ ] Angenommen` footer on inline
  comments (behind `RenderCheckbox`); ticking/unticking it fires
  `pull_request_review_comment`, action `edited`, mapped to `Accepted`/
  `Checkbox` or its undo.
- **GitLab award emoji** — 👍/👎 on a finding's discussion note, matched via
  `PlatformNoteId` (already captured since PR 2a), mapped to
  `Accepted`/`Rejected`/`Emoji` or undo on revoke; the setup wizard will
  additionally subscribe GitLab hooks to `emoji_events`.

None of this is implemented yet — `ResolutionSource` values `"Checkbox"` and
`"Emoji"` and the `Llm` write path are reserved names, not live code paths.
