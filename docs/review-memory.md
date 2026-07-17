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
| **False positive** | Marking a finding in the review detail, or (PR 2, see [Outlook](#outlook-pr-2)) a `@naudit fp` reply on the platform | "This finding (or an equivalent one) is not an issue — don't report it again." Copies the finding's `File`/`Text`; an optional reason can be attached. |
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
  write entries; the platform reply command planned for PR 2 will apply the
  same repo-membership check used elsewhere (fail-closed).
- **Attribution** — every entry records `CreatedBy`.
- **Audit, not deletion** — entries are deactivated (`Active=false`), never
  removed, so a bad entry leaves a trail.

Content is still redacted like every other prompt ingredient, but redaction
targets secrets/IPs/e-mails, not instruction-shaped text — it is not a
defense against injection.

## Outlook: PR 2

This is PR 1 of the review-memory feature (seam, DB, prompt section, WebUI).
PR 2 adds the platform feedback channel: replying `@naudit fp <optional
reason>` on the bot's inline comment creates the same false-positive entry
without leaving the merge request/pull request.

A forward requirement, carried over from the later
`docs/superpowers/specs/2026-07-17-review-analytics-design.md` design (which
builds on top of this feature): when PR 2 starts persisting the platform
comment id for the FP-mapping (`PlatformCommentId`), it should **also** store
the GitLab **note id** alongside the discussion id from the start. GitLab's
discussion-creation response already contains the bot note's id, and a later
feature (award-emoji reactions) needs that note id specifically —
`PlatformCommentId` on GitLab is the *discussion* id, which award-emoji
webhook events do not reference. Capturing both ids in PR 2 avoids a second,
avoidable migration later.
