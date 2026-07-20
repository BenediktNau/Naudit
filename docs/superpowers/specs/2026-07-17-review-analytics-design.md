# Review analytics (finding resolution tracking & acceptance dashboard) — Design

*2026-07-17 · Naudit*

## Problem

Naudit posts findings but never learns what happened to them. There is no way to tell how many
findings were accepted, how many were rejected as false positives, and whether the false-positive
rate improves over time (e.g. once the review memory is in place). CodeRabbit's analytics
(acceptance rate by severity, posted vs. accepted, learnings applied) show what this looks like
when done well. Naudit needs the same insight, derived primarily from **how people react to the
review comments** in the PR/MR.

**Non-goals:** code-based resolution detection (checking whether the next push actually changed
the flagged lines, CodeRabbit-style) — a possible later extension; platform polling of any kind;
automatic creation of memory entries from reactions (the review memory stays human-curated).

## Decisions (settled during brainstorming, 2026-07-17)

1. **Hybrid signals.** Explicit signals (commands, checkbox/emoji, WebUI buttons) always win;
   free-text replies on a finding thread are additionally classified by a small LLM call
   (accepted / rejected / unclear). Explicit beats LLM, never the other way around.
2. **Builds on the review memory** (spec `2026-07-09-review-memory-design.md`, branch
   `feat/review-memory`). Memory PR 1 + PR 2 land first; this feature is PR 3 + PR 4 of that
   track and reuses PR 2's comment→finding mapping (`PlatformCommentId`) and the note/comment
   webhooks verbatim.
3. **Metrics scope (first cut):** base counters + acceptance rate, breakdown by severity,
   weekly trend, and memory impact (entries created/active, times applied).
4. **Explicit in-PR signal: both** a reply command (`@naudit ok`) and one-click platform
   affordances (GitHub: task checkbox in the bot comment; GitLab: 👍/👎 award emoji).
5. **Event-driven, stored in the DB.** Every signal immediately updates the existing
   `ReviewFindingEntity`; the dashboard aggregates via EF queries. No polling, no platform API
   calls at display time, history survives comment deletion.

## Data model (one provider-neutral migration)

`ReviewFindingEntity` gains four nullable columns:

| Column | Meaning |
|---|---|
| `ResolutionStatus` | `"Accepted"` \| `"Rejected"` — null = unanswered |
| `ResolutionSource` | `"Command"` \| `"Checkbox"` \| `"Emoji"` \| `"WebUi"` \| `"Llm"` |
| `ResolvedBy` | platform login or WebUI username |
| `ResolvedAtUtc` | UTC timestamp |

**Precedence rule:** explicit sources (`Command`, `Checkbox`, `Emoji`, `WebUi`) overwrite any
existing status. `Llm` writes only when the status is null or was itself set by `Llm`. Undo
(checkbox unchecked, emoji revoked, WebUI toggle off) resets status/source/by/at to null — but
only when the current source is the one being undone (unchecking a checkbox never clears a
`Command` decision).

`@naudit fp` (memory PR 2) additionally sets `Rejected`/`Command` — one command feeds both the
memory **and** the statistics.

Two further columns in the same migration:

- `ReviewFindingEntity.PlatformNoteId` (nullable): GitLab's award-emoji events reference the
  **note** id, not the discussion id that memory PR 2 stores in `PlatformCommentId`. The
  discussion-creation response contains the bot note's id; memory PR 2 should store both from
  the start (a small amendment to carry into that PR's implementation). On GitHub the
  column stays null (`PlatformCommentId` is already the review-comment id the events carry).
- `MemoryEntryEntity`: `TimesApplied` (int, default 0) + `LastAppliedAtUtc` (nullable) —
  incremented by `DbReviewMemory` when the entry is selected into a prompt. Powers the
  "memory impact" metric (CodeRabbit's "learnings applied").

## Signal capture

All platform signal handling shares memory PR 2's plumbing and rules: same webhook endpoints,
same fail-closed authorization (GitHub `author_association` ∈ {OWNER, MEMBER, COLLABORATOR};
GitLab membership ≥ Developer; unverifiable ⇒ ignore, log, 200), same bot-self filter.

### Reply command `@naudit ok` (PR 3)

Counterpart to `@naudit fp`: replying `@naudit ok` (aliases `angenommen`, `accepted`,
case-insensitive) on the bot's inline comment sets `Accepted`/`Command`. Confirmation reply in
the same thread ("Als angenommen vermerkt."), matching the `fp` confirmation style. Parsed by
the same command parser as `fp` — one more verb, no new webhook surface.

### GitHub checkbox (PR 4)

Bot inline comments end with a task item: `- [ ] Angenommen`. Ticking it edits the comment →
`pull_request_review_comment` webhook, action `edited` (new accepted action next to `created`).
The handler maps the comment id to the finding, diffs the checkbox state, and sets
`Accepted`/`Checkbox` (checked) or performs the undo (unchecked). The editor is the payload
`sender`; authorization as above. Non-checkbox edits and edits of unmapped comments are ignored.
Rendering the checkbox is controlled by `RenderCheckbox` (below) so operators can keep comments
clean.

### GitLab award emoji (PR 4)

GitLab fires **emoji events** (GitLab ≥ 16.2) when an award emoji is added/revoked. The setup
wizard's hook creation adds `emoji_events: true`; existing installations enable it manually
(documented). Handler: event's awardable note id → `PlatformNoteId` → finding; `thumbsup` =
`Accepted`/`Emoji`, `thumbsdown` = `Rejected`/`Emoji`, `revoke` = undo. Other emoji are ignored.
**Stats-only:** a 👎 never creates a memory entry — the memory stays explicitly curated via
`@naudit fp`.

### LLM classification of free-text replies (PR 4)

Any non-command, non-bot reply on a mapped finding thread is classified: the reply text passes
`IPromptRedactor` first (nothing reaches the LLM unredacted), then a minimal classification
prompt goes to the **global** `IChatClient` (never author sessions — a maintainer's reply must
not bill the MR author's subscription): result `Accepted` / `Rejected` / `Unclear`. `Unclear`
writes nothing. The result is applied under the precedence rule (never overwrites explicit).

Because the webhook handler must answer fast and an LLM call is slow, classification runs
through a small bounded `Channel`-based queue + `BackgroundService` (the `ReviewQueue` pattern,
separate queue). Any failure (queue full, LLM error, timeout) is logged and dropped — fail-open,
a statistics signal is never worth a webhook retry storm.

### WebUI buttons (PR 3)

The review detail's finding view gets Accept/Reject actions
(`PUT /api/findings/{id}/resolution`, body `{ "status": "Accepted" | "Rejected" | null }`,
source `WebUi`, `ResolvedBy` = session user; null = undo). Reject offers the existing
"mark as false positive" action (memory PR 1) as an optional follow-up in the same UI spot —
two clicks, deliberately not automatic. Authorization: same project visibility rule as the
dashboard.

### Configuration

New options class bound from `Naudit:Review:Resolution`, all keys in the `SettingsCatalog`
whitelist (non-secret, Settings-page editable):

```csharp
public sealed class ReviewResolutionOptions
{
    public bool Enabled { get; set; } = true;            // master switch for signal capture
    public bool LlmClassification { get; set; } = true;  // free-text classification (PR 4)
    public bool RenderCheckbox { get; set; } = true;     // GitHub checkbox footer (PR 4)
}
```

`Enabled=false` unmaps nothing — webhooks still answer 200 — but signal handling and the
checkbox footer are skipped. The analytics endpoint stays available (it only reads).

## Analytics (API + UI, PR 3)

`GET /api/analytics?projectId=&days=30` — `projectId` optional (omitted = all projects visible
to the caller, aggregated), `days` ∈ {7, 30, 90} (default 30). Visibility rules exactly as the
dashboard (owning account or admin). Response:

- **totals:** `posted`, `accepted`, `rejected`, `unanswered`,
  `acceptanceRate` = accepted ÷ posted, `fpRate` = rejected ÷ posted (both 0 when posted = 0).
  Unanswered counts as not accepted — the rate reflects reality, not politeness.
- **bySeverity:** posted / accepted / rejected per severity (`Critical`/`High`/`Medium`/`Low`).
- **weekly:** ISO-week buckets `{ weekStart, posted, accepted, rejected }` over the range —
  the thesis-relevant trend (does the FP rate drop once the memory kicks in?). Bucketing happens
  in memory over a minimal projection (`CreatedAt`, `Severity`, `ResolutionStatus`) — finding
  volumes are small and SQLite/Postgres date functions diverge; no provider-specific SQL.
- **memory:** `entries`, `active`, `timesApplied` (sum) for the selected project(s).

**SPA:** new nav page **"Auswertung"** (`AnalyticsPage.tsx`), rendered from the existing UI kit —
`StatTile` for the counters/rates, CSS/SVG bars for posted-vs-accepted per severity, `Sparkline`
for the weekly trend, a range selector (7/30/90) and the dashboard's project filter. No new
frontend dependency.

## Error handling & security

- Authorization fail-closed (only members/visible-project users set statuses); processing
  fail-open (a statistics failure never fails a review, a webhook response, or the memory).
- Bot-authored comments, edits, and awards are filtered out (memory PR 2's self-filter).
- Reply texts pass the redactor before the LLM; classification uses the global provider only.
- Cost: one small LLM call per free-text reply on a finding thread; `LlmClassification=false`
  turns it off entirely.
- LLM misclassification is bounded: it can only fill blanks, never override a human's explicit
  decision, and any explicit signal corrects it permanently.

## Testing

- **Precedence/undo matrix** (unit): explicit overwrites LLM, LLM never overwrites explicit,
  undo only clears its own source, `Unclear` writes nothing.
- **Command parsing:** `ok`/`angenommen`/`accepted` aliases both platforms, bot-self filter,
  authorization matrix (fail-closed) — extending memory PR 2's parser tests.
- **GitHub `edited` handling:** checkbox diff (checked/unchecked/non-checkbox edit/unmapped
  comment), sender authorization; signature path via `WebApplicationFactory` (existing pattern).
- **GitLab emoji events:** thumbsup/thumbsdown/revoke/other-emoji, note-id mapping, membership
  fail-closed.
- **LLM classifier** with `FakeChatClient`: all three outcomes, malformed response ⇒ fail-open,
  queue drop on error.
- **Analytics endpoint** against SQLite in-memory: totals/rates (incl. posted = 0), severity
  breakdown, ISO-week bucketing across a year boundary, project visibility (401/403), `days`
  validation.
- **`DbReviewMemory`:** `TimesApplied`/`LastAppliedAtUtc` increment on selection.

## PR slicing

*(after memory PR 1 + PR 2, which this builds on)*

1. **PR 3 — tracking + analytics:** migration (resolution columns, `PlatformNoteId`,
   `TimesApplied`), precedence logic, `@naudit ok` command, WebUI resolution actions,
   `TimesApplied` increment, analytics endpoint + "Auswertung" page, `ReviewResolutionOptions` +
   `SettingsCatalog` keys, `docs/review-analytics.md`, CLAUDE.md extension point. Explicit
   signals usable end-to-end.
2. **PR 4 — convenience signals:** LLM classification (queue + classifier), GitHub checkbox
   (render + `edited` action), GitLab emoji events + wizard hook `emoji_events`, docs update.

## Out of scope (future)

- Code-based resolution detection (did the next push change the flagged lines?).
- Acceptance rate by finding category (findings carry no category today; CodeRabbit's chart
  needs a taxonomy first).
- GitHub reactions as signals (no webhook events exist; would require polling).
- CSV/data export.
