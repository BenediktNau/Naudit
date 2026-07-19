# Architecture profile (distilled guidelines)

## What it is

Naudit's line-level reviews are strong, but a diff-only prompt is structurally blind
to a project's own architecture rules — the ones written down in `CLAUDE.md`,
`AGENTS.md`, or `docs/`, but never part of the diff itself. The **architecture
profile** closes that gap: Naudit auto-distills a compact, maintainer-curatable
summary of a project's *own documented rules* from its own docs, and feeds it into
every review prompt as an authoritative section, right alongside [project
memory](review-memory.md).

Distillation only surfaces rules the documentation already states — it is not
learning from review outcomes (that remains a review-memory non-goal) and it does
not invent conventions. The system prompt (`PromptBuilder.DefaultSystemPrompt`) was
extended alongside this feature to actually ask for what the profile enables: it now
instructs the LLM to also review at the **architecture level** (contract/pattern
breaks, layering violations — findings that often map to no single line, reported
without a line rather than dropped) and to run a fixed **security checklist** (new
endpoints missing auth, injection surfaces, secrets/tokens in code or logs, unsafe
deserialization). Both additions apply regardless of whether a profile is present
for a given project; the profile itself is optional grounding.

## How distillation works

`IReviewGuidelines` (Core `Abstractions`) is the seam:

```csharp
public interface IReviewGuidelines
{
    Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default);
}
```

The default implementation, `DistillingReviewGuidelines`
(`src/Naudit.Infrastructure/Guidelines/DistillingReviewGuidelines.cs`), is called by
`ReviewService` from the same shared checkout used for SAST/context grounding (one
checkout serves all three). It works like this:

1. **Collect sources.** Files are read from the checkout in the configured
   `Naudit:Review:Guidelines:Sources` order (default: `CLAUDE.md`, `AGENTS.md`,
   `README.md`, `CONTRIBUTING.md`, `docs/**/*.md` — the last entry is the only
   glob form supported, matched recursively and sorted for a stable order). A
   running character budget (`MaxSourceChars`, default `60000`) is spent as sources
   are collected; a file that alone would exceed the *remaining* budget is skipped
   **entirely** (never partially included), so the hash and prompt stay
   deterministic regardless of which files happened to fit.
2. **Hash the collected sources.** A SHA256 hash is computed over the concatenated
   `path + content` of every collected source, in that same stable order.
3. **Cache on the hash.** If a profile is already stored for the project and its
   stored hash matches, the stored profile is returned **with zero LLM calls** —
   distillation only happens when the documentation actually changed.
4. **Distill.** On a hash mismatch (or no stored row yet), each source is passed
   through `IPromptRedactor` individually (masking secrets/IPs/e-mails in the
   *source* text, same as every other prompt ingredient) and concatenated under a
   `## <path>` heading. This goes to the **global** `IChatClient` — the same
   singleton `ReviewService` falls back to when author-session routing is off,
   injected directly into `DistillingReviewGuidelines`, never through
   `IAiClientRouter`. Distillation never rides an author's own subscription. The
   system prompt asks for 10-20 binding, enforceable rules as a terse Markdown
   bullet list, instructed to invent nothing; an empty response is honored as "no
   rules found" rather than treated as an error.
5. **Store.** The result (including an **empty** distillate) is written back with
   its hash, `DistilledAt`, and `UpdatedBy = "naudit"`. Storing the empty case is
   deliberate: it caches the hash so an empty-but-unchanged repo does not re-run the
   LLM call on every review. The stored/returned profile is also capped at
   `MaxProfileChars` (default `4000`; truncated, not re-summarized, if the model's
   answer is longer).

Redaction therefore runs **twice** on the way to a reviewer: once per source file
before it reaches the distillation prompt, and again on the *distilled profile*
itself before it is inserted into the actual review prompt (`ReviewService`
redacts `guidelines` exactly like the diff, findings, title, context, and memory —
see [Prompt redaction](redaction.md)). A stored, human-edited profile still passes
through that second redaction pass on every review, even though it skips
distillation.

## Curation

The Memory page (top-nav "Memory", per project — see [Review
memory](review-memory.md)) shows an **"Architecture profile" card** above the
memory entries:

- Displays the current markdown, tagged `distilled` or `curated` with `UpdatedBy`
  and the last-updated date.
- **Edit.** `PUT /api/projects/{id}/guidelines` overwrites the stored markdown and
  sets `ManuallyEdited = true`. From that point, `DistillingReviewGuidelines` never
  overwrites the row again on its own — human curation always wins over the
  auto-distilled version, indefinitely.
- **Staleness hint.** While a profile is `ManuallyEdited`, a source hash mismatch
  (the repo's docs changed since curation) does not re-distill; instead the row's
  `SourcesChangedAt` is stamped once, and the WebUI shows a warning ("Repository
  docs changed since this profile was curated — 're-distill' to rebuild it.") next
  to the card without touching the curated text.
- **Re-distill.** `POST /api/projects/{id}/guidelines/redistill` resets
  `ManuallyEdited = false`, clears `SourceHash` to `""`, and clears
  `SourcesChangedAt`. This performs no LLM call itself (the WebUI has no
  checkout to distill from) — it just guarantees the next hash comparison
  mismatches, so the **next review** rebuilds the profile from the repo's current
  docs.

## First review of a project

On a project's very first review, no `ProjectEntity` row exists yet —
`EfReviewAuditSink` only creates it *after* the review is posted. Since
`DistillingReviewGuidelines.GetAsync` looks the project up by `projectId` before
touching `ProjectGuidelines`, an unknown project still gets a freshly distilled
profile for that first review (findings from it are not suppressed), but the
result is **not persisted** — there is no project row to attach it to yet, and the
distiller does not duplicate the audit sink's project-creation/ownership logic to
work around that. From the **second** review onward the project row exists, so the
profile is stored and the hash cache applies as normal. The cost of this is one
repeat distillation call on review 2 (a deliberate, accepted trade-off — see the
design spec's self-review).

## Degradation

Every failure mode fails open — a broken profile pipeline never blocks or fails a
review:

- **No checkout at all** (no SAST analyzer configured and `Review:Context:Enabled
  = false`, or the checkout itself failed) ⇒ `workspaceDir` is `null`;
  `GetAsync` returns the stored profile **only if it was manually curated**
  (`ManuallyEdited`), without an LLM call. A purely machine-distilled profile is
  *not* used here: without a workspace its source hash cannot be re-verified, so
  it might have been distilled from an unmerged PR's docs (see the trust section
  below).
- **No source files found** in the checkout ⇒ same fallback: the stored profile
  (if any), no LLM call.
- **The distillation LLM call throws** ⇒ the stored profile (if any) is returned;
  the row is left untouched (no partial write).
- **Any other exception** in `GetAsync` (DB unavailable, etc.) is caught, logged,
  and `null` is returned — the review proceeds with no guidelines section at all,
  exactly like the feature being off.

In every fallback case, "no stored profile" simply means no guidelines section is
rendered — `PromptBuilder` only emits the "Project guidelines" section when the
value is non-null/non-whitespace, so the prompt stays byte-identical to the
guidelines-off case.

## Trust model: sources come from the reviewed PR's checkout

Sources are collected from the **same checkout that is being reviewed** — i.e.
potentially from an unmerged branch. A PR that edits `CLAUDE.md`/`docs/**.md`
can therefore influence the profile that gets distilled and stored during its
own review. Three properties bound that risk:

- **Hash self-healing:** the stored profile is only *used* by a review whose own
  checkout produces the same source hash. The next review of any clean PR sees
  the unmodified docs, gets a hash mismatch, re-distills from the clean sources
  and overwrites the stored row. A poisoned profile therefore never reaches the
  prompt of another PR's review (as long as reviews run with a checkout, which
  is the default).
- **Curation wins:** a `ManuallyEdited` profile is never overwritten by
  distillation — poisoning cannot displace human curation.
- **No-checkout guard:** without a workspace, only curated profiles are trusted
  (see Degradation above) — a machine-distilled profile whose hash cannot be
  re-verified is not injected.

Residual exposure: the WebUI shows the last-stored profile, so a maintainer
could see (and curate on top of) content distilled from a malicious PR between
that PR's review and the next clean review — the card's `distilled · naudit ·
date` provenance line is the tell. Distilling from the *target* branch instead
would remove this entirely but requires a second checkout per review; revisit
if it becomes a real problem.

One more small window exists on the curation side: a `PUT` on a project without
a prior distillation stores `SourceHash = ""` (the endpoint has no checkout, so
there is no real baseline). The next review adopts the docs it sees as the
baseline without raising `sourcesChangedAt`. Docs that change *between* the PUT
and that next review are treated as the baseline rather than as a change — the
alternative (flagging "docs changed" for every first curation) would be wrong
100% of the time, so the rare swallowed signal is the better trade-off.

## Configuration

```jsonc
"Naudit": {
  "Review": {
    "Guidelines": {
      "Enabled": true,           // default ON; false ⇒ NullReviewGuidelines (always null, no-op)
      "MaxSourceChars": 60000,   // cap on the combined size of collected source files
      "MaxProfileChars": 4000    // cap on the stored/injected profile
      // "Sources" is env-only, see below
    }
  }
}
```

`Enabled`, `MaxSourceChars`, and `MaxProfileChars` are DB-managed (Settings page)
via `SettingsCatalog` like most other `Naudit:Review:*` keys. `Sources` is
**list-shaped** and stays env/appsettings-only on purpose, the same convention as
`ProjectTokens`/`Ui:Admins` — see [Configuration](configuration.md). Its default is
`["CLAUDE.md", "AGENTS.md", "README.md", "CONTRIBUTING.md", "docs/**/*.md"]`; order
is priority order for the character budget, and only a trailing `dir/**/*.md`
pattern is supported (recursive, alphabetically sorted) — any other entry is
treated as an exact repo-relative path.

## API

All three routes live in `src/Naudit.Web/Endpoints/GuidelinesEndpoints.cs`, are
mapped under `/api` with `RequireAuthorization()`, and use the same
visibility/authorization as the rest of the JSON API: the session account must be
able to see the project (own account or admin) or the call returns `403`; an
unknown project id returns `404`.

| Route | Purpose |
| --- | --- |
| `GET /api/projects/{id}/guidelines` | Current profile: `{ markdown, distilledAt, manuallyEdited, sourcesChangedAt, updatedBy, pending }` — all fields `null`/`false` if no row exists yet; `pending = true` after a redistill until the next review has re-distilled (the card then shows the old markdown as "re-distills on the next review") |
| `PUT /api/projects/{id}/guidelines` | Manually set the profile: `{ "markdown": "..." }`; `400` if empty or over `MaxProfileChars`; sets `manuallyEdited = true` |
| `POST /api/projects/{id}/guidelines/redistill` | Reset curation flags so the next review re-distills from the repo's current docs; idempotent, no-op-but-`200` if no row exists yet |
