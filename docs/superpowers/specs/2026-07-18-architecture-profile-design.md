# Architecture profile (distilled guidelines) & review altitude — Design

*2026-07-18 · Naudit*

## Problem

Naudit's reviews are strong at the line level but blind at the architecture level. The PR #63
comparison made this concrete: Naudit found six real line-level robustness bugs, while CodeRabbit
found the one architectural finding that mattered most — the new webhook branch violated the
project's own "webhook endpoints enqueue and return 200 immediately" rule. CodeRabbit found it
because it had the repository's coding guidelines as a review reference. Naudit never sees them:
`CLAUDE.md` and `docs/` are not part of the prompt, and the system prompt's "be concise" /
"findings tied to a line" instructions structurally bias the model toward line nits.

Two gaps, one slice:

1. **No guidelines input.** The project's own documented architecture rules never reach the LLM.
2. **No altitude instruction.** The prompt never asks for architecture-level or security-surface
   findings, and treats file-less findings as an edge case rather than an expected output.

**Non-goals:** a second review pass (only if evidence later shows this slice is insufficient),
agentic repo navigation, DAST, auto-learning from review behaviour (still a review-memory
non-goal — distilling *documented* rules is not learning from outcomes).

## Decisions (settled during brainstorming, 2026-07-18)

1. **Optimize for product usefulness**, not short-term benchmark deltas; keep token usage as low
   as possible with a hard ceiling of ~2× today's per-review cost.
2. **Guidelines source: auto-distilled, DB-held, curatable ("B2").** Naudit distills a compact
   architecture profile from the repository's own docs and keeps it current itself — no
   maintainer-authored guidelines file required (zero setup, no adoption hurdle). A dedicated
   `.naudit/` guidelines file and auto-detection of `AGENTS.md`-style files were considered and
   rejected for now (adoption hurdle / token ballast); either can be added later as an additional
   source.
3. **One profile blob per project, human curation wins.** Not per-rule entries. Once a maintainer
   edits the profile, auto-refresh stops (see Curation policy).
4. **Prompt changes ship in the same slice:** an authoritative "Project guidelines" section plus
   altitude and security-checklist instructions in the system prompt. The verdict stays derived
   from the existing severity gate; guideline violations are ordinary findings.

## Architecture

Same seam pattern as `IReviewMemory` / `IPromptRedactor`: interface in Core, implementation in
Infrastructure, selection in `AddNauditInfrastructure`. Core keeps depending on MEAI
abstractions only.

### Core additions

```csharp
// Naudit.Core.Abstractions
/// <summary>Liefert das Architektur-Profil (destillierte Projekt-Guidelines) für ein Review.</summary>
public interface IReviewGuidelines
{
    // workspaceDir = der geteilte Checkout (null, wenn kein Checkout stattfand) —
    // die Implementierung destilliert daraus bzw. liefert das gespeicherte Profil.
    Task<string?> GetAsync(string projectId, string? workspaceDir, CancellationToken ct = default);
}
```

`ReviewOptions` gains a `Guidelines` sub-options class (bound from `Naudit:Review:Guidelines`):

```csharp
public sealed class ReviewGuidelinesOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxSourceChars { get; set; } = 60_000;   // Deckel für den Destillat-Input
    public int MaxProfileChars { get; set; } = 4_000;   // Deckel für das gespeicherte/eingespeiste Profil
    public List<string> Sources { get; set; } = new()   // relativ zum Repo-Root; Reihenfolge = Priorität
        { "CLAUDE.md", "AGENTS.md", "README.md", "CONTRIBUTING.md", "docs/**/*.md" };
}
```

### Infrastructure additions

**Entity** (`ProjectGuidelinesEntity` in `Data/Entities.cs`; migration hand-neutralized for
SQLite + Postgres like the existing ones):

| Column | Notes |
|---|---|
| `Id` | PK |
| `ProjectId` | FK → `ProjectEntity`, unique (one profile per project) |
| `Markdown` | the distilled profile (capped at `MaxProfileChars`) |
| `SourceHash` | SHA256 over the concatenated source-file contents that produced it |
| `DistilledAt` | UTC of the last distillation |
| `ManuallyEdited` | bool — set on first WebUI edit; blocks auto-refresh from then on |
| `SourcesChangedAt` | nullable UTC — set when a review observes a source-hash mismatch while auto-refresh is blocked (drives the WebUI staleness hint); cleared on edit and re-distill |
| `UpdatedBy` | WebUI username of the last manual edit, or `"naudit"` for distillations |

**`DistillingReviewGuidelines`** (`src/Naudit.Infrastructure/Guidelines/`) — the default
implementation. `GetAsync` logic:

1. Load the project's stored profile row (project looked up by `PlatformProjectId`; none ⇒ no
   stored profile).
2. `workspaceDir == null` (no checkout: SAST *and* context disabled) ⇒ return the stored
   profile's `Markdown` or null. Degrades gracefully; never distills without sources.
3. Collect source files from the checkout per `Sources` (known names first, then `docs/**/*.md`;
   total input capped at `MaxSourceChars` — files beyond the cap are skipped whole, in list
   order). No sources found ⇒ return stored profile or null.
4. Compute `SHA256` over the concatenated (path + content) of the collected sources.
   Hash equals stored `SourceHash` ⇒ return stored `Markdown` (steady state: **zero LLM calls**).
5. Hash differs and `ManuallyEdited == true` ⇒ return the stored (edited) `Markdown` unchanged —
   human curation wins; the WebUI shows a "docs changed — re-distill?" hint instead (the stored
   `SourceHash` staying stale *is* the staleness signal).
6. Hash differs and not manually edited ⇒ **distill**: redact the collected sources
   (`IPromptRedactor`), send one prompt to the **global** `IChatClient` ("extract the 10–20
   binding architecture/convention rules of this project as a terse Markdown bullet list; only
   rules stated in the docs, no inventions"), cap the output at `MaxProfileChars`, upsert the
   row (`SourceHash`, `DistilledAt`, `UpdatedBy = "naudit"`), return it. The distillation
   deliberately bypasses `IAiClientRouter` — author-session routing is a per-review concern;
   the profile is project-infrastructure.
7. **Fail-open everywhere:** any error (IO, DB, LLM) is caught, logged, and falls back to the
   stored profile or null — a guidelines hiccup never fails or delays-beyond-timeout a review
   (audit-sink philosophy). Real cancellation propagates.

`Naudit:Review:Guidelines:Enabled=false` registers `NullReviewGuidelines` (always null) —
exactly the redaction/memory pattern. `Enabled` / `MaxSourceChars` / `MaxProfileChars` join the
`SettingsCatalog` whitelist (non-secret); `Sources` stays env-only (list-shaped, like
`ProjectTokens`).

## Request-flow changes (`ReviewService`)

`ReviewService` gains an `IReviewGuidelines` constructor dependency and calls `GetAsync` after
the checkout is available (alongside memory selection). The returned Markdown passes through
`IPromptRedactor` like diffs/findings/memory, then goes into `PromptBuilder.Build` as a new
optional `string? guidelines = null` parameter. First review of a project pays the one inline
distillation call (seconds; within the 2× ceiling); subsequent reviews pay only the ~4k-char
profile — and no LLM call at all while the docs are unchanged.

## Prompt changes (`PromptBuilder`)

**New section**, rendered between the tool-guidance and memory sections (memory stays last):

```text
# Project guidelines (distilled from this repository's own documentation; maintainer-curated, authoritative)

<profile markdown>
```

Empty/null guidelines render nothing — the prompt is byte-identical to today.

**`DefaultSystemPrompt` additions** (effective even before a profile exists):

- *Altitude:* "Also review the change at the architecture level: violations of the Project
  guidelines section, breaks of contracts or patterns the codebase itself establishes, and
  layering violations. Such findings often map to no single changed line — report them without
  a line (omit \"line\") rather than dropping them."
- *Security checklist:* "For security, check specifically: new endpoints/handlers for missing
  authentication or authorization, injection surfaces (SQL/command/path/SSRF), secrets or
  tokens in code or logs, and unsafe deserialization."

The verdict mechanism is unchanged: guideline/architecture findings carry severity/confidence
like any finding and flow through the existing gate. File-less findings are already supported
end-to-end (they render in the summary comment, not inline).

## WebUI

The existing Memory page gains an **"Architecture profile" card** at the top (no new nav item):

- shows the profile Markdown (rendered + editable), `DistilledAt`, and whether it was manually
  edited;
- **Edit** saves via `PUT /api/projects/{id}/guidelines` (`{ "markdown": "..." }`, capped at
  `MaxProfileChars`, sets `ManuallyEdited = true`, `UpdatedBy` = session user);
- **Re-distill** button posts `POST /api/projects/{id}/guidelines/redistill`. Distillation
  needs a repo checkout, which the WebUI doesn't have — so to keep the slice lean the endpoint
  does **not** run an inline LLM call; it resets `ManuallyEdited` and clears `SourceHash`,
  which forces re-distillation on the next review (step 4/6: cleared hash never matches).
  The card explains this ("profile refreshes on the next review"). Overwrites edits, so the
  frontend confirms first.
- a staleness hint ("repository docs changed since this profile was distilled") when
  `SourcesChangedAt` is set (see entity table — written by the distiller in step 5).
- `GET /api/projects/{id}/guidelines` returns the card's data. Authorization mirrors the memory
  routes: visible project (owning account or admin), 401/403 as JSON, fail-closed.

## Error handling & security assumptions

- Distillation failure, missing docs, missing checkout ⇒ fail-open (stored profile or nothing),
  review proceeds; errors logged.
- The profile is LLM- or human-authored text that lands in every future prompt — same accepted
  prompt-injection surface as memory entries, mitigated the same way: only repo docs (already
  maintainer-controlled) feed the distiller, the result is visible and editable in the WebUI,
  and everything is attributed (`UpdatedBy`) and capped.
- Redaction applies twice: to the distiller's input sources and to the profile on prompt
  insertion (cheap; consistent with every other prompt ingredient).
- A wrong distilled rule acts systematically — the curation card is the correction path; the
  distillation prompt forbids inventing rules not present in the docs.

## Testing

- **`DistillingReviewGuidelines`** (FakeChatClient + temp dir as workspace): distills on first
  sight; identical sources ⇒ no second LLM call (call-count assert); changed sources ⇒
  re-distills; `ManuallyEdited` blocks auto-refresh and sets `SourcesChangedAt`; no
  checkout ⇒ stored profile; no docs ⇒ null; LLM error ⇒ stored/null + log (fail-open);
  input/output caps enforced.
- **Litmus test:** a mini doc containing the webhook-enqueue rule ⇒ the profile passed to the
  prompt contains it (FakeChatClient echo).
- **`PromptBuilder`:** renders the section; null/empty ⇒ byte-identical prompt; system prompt
  contains the altitude + security additions.
- **`ReviewService`** (new `FakeReviewGuidelines`): profile appears in the prompt and passes the
  redactor; guidelines provider throwing is not a concern here (fail-open lives in the impl).
- **API endpoints:** GET/PUT/redistill happy paths, caps, 401/403 matrix (mirrors memory-route
  tests), `ManuallyEdited`/`SourceHash` state transitions.

## Rollout & docs

One PR: seam + entity/migration + distiller + prompt changes + WebUI card + config keys +
`docs/review-guidelines.md` + CLAUDE.md extension-point entry. No changes to webhooks, gate, or
platforms. Roadmap context (agreed 2026-07-18): this is slice 1; slice 2 = review-analytics
(PRs 3+4, already designed), slice 3 = `@naudit` dialog commands on the PR-2b infrastructure,
slice 4 = eval harness / golden set; a dedicated second architecture pass stays
evidence-gated.

## Out of scope (future, same seam)

- `.naudit/guidelines.md` as an explicit-priority source (verbatim, skips distillation).
- Auto-detected extra sources beyond the configured list.
- A second, architecture-only review pass (~2× cost — only if this slice proves insufficient).
- Per-rule granularity, rule fingerprinting, or suppression of individual distilled rules.
- Distillation via author sessions or a cheaper dedicated model.
