# Review memory (project-level maintainer guidance)

Naudit gives a reviewing LLM one more thing besides the diff, static-analysis
findings, and repo context: a **per-project memory** of two human-curated kinds
of guidance — findings a maintainer has marked as **false positives**, and
free-text **project conventions**. Both are injected into the prompt as a
read-only section so the LLM stops repeating mistakes it already made and
respects rules that only a human knows (an unusual but intentional pattern,
a deprecated API kept around on purpose, etc.).

Nothing here changes the merge decision mechanically: memory only shapes what
the LLM says. The verdict is still **derived from the LLM's own findings**
via the severity-aware gate (see [Review gate](review-gate.md)) — there is no
post-LLM filtering step that removes findings matching a memory entry. A
false-positive entry works by *persuading the model not to raise the finding
again*, not by suppressing it after the fact.

## What gets remembered

| Kind | Created by | Meaning |
| --- | --- | --- |
| **False positive** | Marking a finding in the review detail, or (PR 2b, see [Outlook](#outlook-pr-2b)) a `@naudit fp` reply on the platform | "This finding (or an equivalent one) is not an issue — don't report it again." Copies the finding's `File`/`Text`; an optional reason can be attached. |
| **Convention** | Typed on the project's Memory page | A free-text project rule, optionally scoped to a file/path. "German code comments are intentional", "this legacy module intentionally skips null checks", etc. |

Entries are **never deleted** by the WebUI actions — deactivating (or undoing
an FP mark) just flips an `Active` flag, so there is always an audit trail of
who curated what and when.

## How entries reach the prompt

`ReviewService` calls `IReviewMemory.SelectAsync(projectId, changes)` right
after fetching the diff and running any SAST/context grounding (memory
selection itself needs no repo checkout, so it still runs when SAST/context
are disabled). Selection is deterministic — no embeddings, no ranking model:

- the project is looked up by its platform project id; an unknown project
  (nothing reviewed yet) selects nothing;
- **all active conventions** are included (a convention's `File` is a display
  hint for the LLM, never a selection filter);
- **active false positives** are included when they are file-less, or when
  their `File` exactly matches a changed file in this diff;
- the combined list (conventions, then false positives — each newest-first)
  is capped at `MaxEntries`.

Selected entries pass through `IPromptRedactor` exactly like the diff, the
findings, and the title — memory text is redacted before it ever reaches the
LLM, no exception (see [Prompt redaction](redaction.md)).

`PromptBuilder` renders the result as the **last** section of the prompt,
deliberately closest to the response — instructions here carry the most
weight:

```text
# Project memory (maintainer guidance)

## Known false positives — do NOT report these or equivalent findings again
- src/Foo/Bar.cs: <finding text> (maintainer note: <reason>)
- <file-less entry text>

## Project conventions — respect these when judging the diff
- <convention text>
- src/Legacy/: <convention text scoped to a file>
```

An empty memory list renders nothing — the prompt is byte-identical to a
review with the feature off.

## Configuration

```jsonc
"Naudit": {
  "Review": {
    "Memory": {
      "Enabled": true,     // default ON; false ⇒ NullReviewMemory (today's behaviour, no-op)
      "MaxEntries": 50     // cap on entries injected per review
    }
  }
}
```

Both keys are DB-managed (Settings page) like most other `Naudit:Review:*`
keys — see [Configuration](configuration.md). `Enabled=false` swaps in
`NullReviewMemory`, which always returns an empty list; this is the same
seam pattern as `IPromptRedactor`/`NullPromptRedactor`.

## WebUI flows

- **Review detail — FP toggle.** Every finding gets an **"FP"** button next
  to it. Clicking it calls `POST /api/findings/{id}/false-positive` (optional
  body `{ "reason": "..." }`), creates a false-positive memory entry from that
  finding, and the button switches to **"FP ✓"**. Clicking it again calls
  `DELETE /api/findings/{id}/false-positive`, which deactivates the entry
  (never deletes it). Marking the same finding twice is idempotent — the
  existing entry is reactivated/updated, not duplicated.
- **Memory page** (top-nav "Memory", per project). Lists every entry for the
  selected project, including inactive ones, tagged `FP` or `convention`
  with author and date. A small form creates new conventions
  (`POST /api/projects/{id}/memory`, body `{ "text": "...", "file": null }`);
  every entry has an activate/deactivate toggle
  (`PUT /api/memory/{id}`, body `{ "active": bool }`).

Authorization on all of the above matches the rest of the JSON API: the
session account must be able to see the project (own account or admin), else
401/403 — same rule as the dashboard.

## API summary

| Route | Purpose |
| --- | --- |
| `POST /api/findings/{id}/false-positive` | Mark a finding as a false positive (creates or reactivates the memory entry); optional `{ "reason": "..." }` |
| `DELETE /api/findings/{id}/false-positive` | Undo — deactivates the entry (idempotent, no entry ⇒ still `204`) |
| `GET /api/projects/{id}/memory` | List all memory entries for a project, including inactive |
| `POST /api/projects/{id}/memory` | Create a convention entry: `{ "text": "...", "file": null }` |
| `PUT /api/memory/{id}` | Activate/deactivate any entry: `{ "active": bool }` |

## Fail-open behavior

Memory selection can fail (DB hiccup, corrupt row) without failing the
review: `DbReviewMemory.SelectAsync` catches any exception, logs a warning,
and returns an empty list — the review proceeds with no memory guidance,
exactly like a false positive that was never marked. This mirrors the
audit-sink philosophy used elsewhere in Naudit: a secondary feature must
never take down the primary one.

## Accepted risk: prompt injection via memory text

Memory entries are **user-authored free text that lands directly in the
prompt**. A maintainer (or anyone with write access to memory) could phrase
an entry to try to steer the LLM beyond its intended scope — this is a form
of prompt injection, and it is an **accepted risk**, not something Naudit
detects or blocks. It is mitigated the same way any other privileged action
in Naudit is:

- **Authorization** — only accounts that can see the project (WebUI) can
  write entries; the platform reply command planned for PR 2b will apply the
  same repo-membership check used elsewhere (fail-closed).
- **Attribution** — every entry records `CreatedBy`.
- **Audit, not deletion** — entries are deactivated (`Active=false`), never
  removed, so a bad entry leaves a trail.

Content is still redacted like every other prompt ingredient, but redaction
targets secrets/IPs/e-mails, not instruction-shaped text — it is not a
defense against injection.

## Comment mapping (foundation for the reply command)

Every finding Naudit posts as an inline comment also stores the platform
id(s) of that comment, so a later reply on it can be mapped back to the
finding it belongs to:

- `ReviewFindingEntity.PlatformCommentId` — GitHub: the review-comment id;
  GitLab: the discussion id.
- `ReviewFindingEntity.PlatformNoteId` — GitLab: the id of the discussion's
  root note (award-emoji webhook events reference the note, not the
  discussion); GitHub: always `null` (no separate note concept).

Both columns are `null` for orphan findings (no diff position, so nothing was
ever posted as an inline comment for them), and can also be `null` for a
positioned finding if id capture itself failed.

The Core seam is `IGitPlatform.PostReviewAsync`, which now returns
`IReadOnlyList<PostedComment>` (`Naudit.Core.Models.PostedComment(string?
CommentId, string? NoteId)`) — one entry per input inline comment,
index-aligned with it. `ReviewService` zips `posted[i]` onto `inline[i]` when
building the audit findings for `RecordAuditAsync`; `EfReviewAuditSink`
persists the pair onto the corresponding `ReviewFindingEntity` row.

Capture is platform-specific and **best-effort**: a capture failure never
fails the already-posted review, it only yields `null` ids for the affected
comment(s).

- **GitLab** reads the discussion id and its first note's id straight out of
  the `POST …/discussions` response body for each inline comment; an
  empty/unexpected body (e.g. in tests, or an older GitLab version) is caught
  and yields `null` ids for that comment.
- **GitHub**'s `POST …/reviews` response does not carry a per-comment id, so
  after posting, Naudit reads back the review's own id and calls
  `GET …/reviews/{id}/comments`, matching each posted inline comment to a
  returned comment by (`path`, `line`) to read its id. Any failure in that
  follow-up call (network error, unexpected shape) is caught and yields
  `null` ids for every comment of that review — the review itself is already
  posted and stays posted either way. That read-back is a single
  `per_page=100` page (the same POC cap as elsewhere), so on a review with
  more than 100 inline comments the overflow comments get `null` ids;
  consumers must treat a `null` id as "unmapped", never as an error.

This mapping (PR 2a) is a small, self-contained change with no consumer yet
— it is the anchor two follow-on features build on: PR 2b, the `@naudit fp`
reply command below, and the review-analytics feature
(`docs/superpowers/specs/2026-07-17-review-analytics-design.md`), both of
which need a stored comment id to correlate later platform activity back to
the finding that caused it.

## Outlook: PR 2b

This is PR 2a of the review-memory feature (PR 1 was the seam/DB/prompt
section/WebUI from the earlier section above; PR 2a is the comment→finding
mapping described just above). PR 2b adds the platform feedback channel:
replying `@naudit fp <optional reason>` on the bot's inline comment creates
the same false-positive entry without leaving the merge request/pull
request, resolving the reply back to its finding via the
`PlatformCommentId`/`PlatformNoteId` captured above.
