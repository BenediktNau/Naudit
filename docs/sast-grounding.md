# SAST/SCA grounding

Naudit can clone the MR/PR head and run static-analysis (SAST) and dependency
(SCA) scanners, then feed the normalized findings into the review prompt as
**grounding**. The LLM still produces the single verdict — tools never block on
their own (no hard tool gate).

## Configuration (`Naudit:Sast`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch. `false` ⇒ exact diff-only behavior. |
| `Analyzers` | `["opengrep","trivy"]` | Active analyzers by name. |
| `OpengrepRules` | _(empty)_ | Extra `--config` paths for OpenGrep, **added on top** of the defaults (see below). |
| `Reducer` | `deterministic` | Finding de-duplication strategy (seam for a future `llm` reducer). |
| `AnalyzerTimeout` | `00:05:00` | Per-tool timeout. |
| `MaxFindingsPerGroup` | `20` | Cap per category for findings on a commentable diff line (tier 0, see [Pre-existing findings](#pre-existing-findings) below). |
| `MaxPreExistingPerGroup` | `5` | Separate, smaller cap per category for findings in a changed file but outside the hunks (tier 1) — its own budget so baseline findings never crowd out diff findings. |

`Enabled`, `Analyzers`, `Reducer`, `AnalyzerTimeout`, `MaxFindingsPerGroup` and
`MaxPreExistingPerGroup` are DB-managed: the WebUI has a "Static analysis (SAST)" panel under
**Settings → Review rules** (switch plus one checkbox per analyzer), and changes apply after
the restart triggered from the same page. `Analyzers` is list-shaped — one comma-separated row
in the database, indexed syntax when set via environment (`Naudit__Sast__Analyzers__0=opengrep`),
which then locks the field in the UI (see [List-shaped settings](configuration.md#list-shaped-settings)).
An unknown analyzer name is rejected by the Settings API; `OpengrepRules` stays
env/appsettings-only.

## Analyzers

- **opengrep** — SAST, multi-language, no build (does not execute repo code).
  OpenGrep is the fully-LGPL fork of Semgrep; we run it with **pinned, explicit
  rule paths** — never `--config auto` (that would pull the license-restricted
  Semgrep registry rules and send telemetry).
- **trivy** — SCA/dependency CVEs, multi-ecosystem, no build.
- **osv-scanner** — SCA via Google's OSV database (`osv-scanner scan source`),
  language-agnostic over lockfiles/manifests, no build. Complements trivy. CVE in
  `RuleId`, package/version in the message; severity mapped from the CVSS score.
- **betterleaks** — secrets detection (`betterleaks dir`), no build. The maintained,
  gitleaks-CLI-compatible successor by the original Gitleaks author (replaces the
  bundled `gitleaks` binary). Findings carry the rule, file and line only — the
  **raw secret value is never forwarded** into the prompt or logs. Reported under the
  `Secrets` category.
- **dotnet-sca** — `.NET` SCA via `dotnet list package --vulnerable`. **Opt-in:**
  it runs `dotnet restore`, which **executes the reviewed code's build logic**,
  and it needs the .NET SDK in the image (the default runtime image only ships
  opengrep + trivy). Enable only for trusted repos and an SDK-based image.

### OpenGrep rules

By default OpenGrep runs the **whole** pinned rule set — **all ~30 languages**
OpenGrep supports — so you do **not** need to configure anything per language. The
image ships two rule sources, both **pinned**:

- `opengrep/opengrep-rules` (LGPL-2.1) at a pinned commit, under `/opt/opengrep-rules`
  (≈2000 rules across all languages). The Docker build strips the repo's non-rule
  YAML (`.github/`, `stats/`, `.pre-commit-config.yaml`) — a single non-rule YAML in
  the `--config` tree would otherwise abort the entire scan.
- Naudit's own `.NET`/C# security overlay under `/opt/naudit-rules` (repo: `sast/rules/`).

Both defaults **always run**. `Naudit:Sast:OpengrepRules` lets a deployment **add**
extra `--config` paths (e.g. an in-house rule directory) on top of the defaults —
they are appended, not replaced, so the overlay can never be dropped by accident.
OpenGrep only applies rules matching each file's language, so the full set adds no
noise for languages a repo doesn't use.

## Behavior

- All findings are collected **repo-wide**, then classified into three tiers
  (see [Pre-existing findings](#pre-existing-findings)): tier 0 (on a commentable
  diff line), tier 1 (elsewhere in a changed file) and tier 2 (in an untouched
  file). Only tier 0 and tier 1 can appear as individual findings in the
  prompt, each annotated `[in diff]` vs `[pre-existing]`; tier 2 never does.
- Findings are de-duplicated, sorted tier-first then by severity, and capped
  **per category and per tier** before grounding (`MaxFindingsPerGroup` for
  tier 0, `MaxPreExistingPerGroup` for tier 1) — a busy baseline can never
  crowd diff findings out of their own budget.
- Graceful degradation: a single analyzer failure is logged and skipped; a failed
  checkout degrades the review to diff-only (it does not fail the gate).
- The system prompt instructs the model to treat the toolchain/target framework
  as valid and current (mitigates outdated-knowledge false positives).

## Pre-existing findings

Everything a scan turns up that is **not** on a commentable diff line — tier 1 and
tier 2 findings — is never dropped, but it is also never dumped into the prompt
as a wall of individual findings. Instead it is condensed by `PreExistingSummary`
(`src/Naudit.Core/Review/PreExistingSummary.cs`) into one deterministic overview
that two things are rendered from.

**The three tiers**, decided per finding by `ReviewService.Annotate` before the
reducer ever sees them:

| Tier | Meaning |
| --- | --- |
| 0 (`InDiff`) | On a commentable diff line — an **added or a context line inside a hunk**. A finding with **no line number** in a changed file (typical for an SCA finding on a lockfile) also counts as tier 0: there is no hunk to place it outside of, and a dependency finding in a lockfile the MR touches is not "pre-existing". |
| 1 (`InChangedFile`, not `InDiff`) | In a file the MR changed, but on a line outside every hunk. |
| 2 (neither) | In a file the MR did not touch at all. |

The reducer (`DeterministicFindingReducer`) sorts **tier before severity** and
caps tier 0 and tier 1 **separately** (`MaxFindingsPerGroup` / `MaxPreExistingPerGroup`,
per category) so pre-existing findings can never use up a budget slot a diff
finding needed. Tier 2 findings never get an individual prompt slot at all — the
reducer routes the **entire, uncapped** set of tier-1 + tier-2 findings (i.e.
everything with `InDiff == false`) into `PreExistingSummary.Build` instead.

**Severity-dependent condensation.** `PreExistingSummary.Build` is deliberately
**not** a flat top-N cut: findings at or above `Naudit:Review:PreExisting:DetailSeverity`
(default `High`) are listed **individually with file and line**; everything below
that is grouped **by rule** (rule id, severity, count, one example location).
The reasoning is measured, not assumed: a full scan of [cal.com](https://github.com/calcom/cal.com)
with the **entire** OpenGrep rule tree (no tuning) returns **2,817 findings spread
across only 48 distinct rules** — the top 5 rules alone account for 91.6% of them,
and a single lint-style rule (`i18next-key-format`) is 62.6% (1,763 hits) on its
own. A plain "top N findings" cut would either be dominated by that one rule or
have to explicitly exclude it. The entire ERROR/High tier of that same scan, in
contrast, is **21 findings across 7 rules** — small enough to name individually.
Grouping by rule (rather than capping by count) keeps the summary's size bounded
by the number of *distinct problems*, not the number of *occurrences*, and keeping
the high-severity tier always individual means a real security issue sitting in
untouched code can never disappear behind i18n/lint noise.

**Two consumers, one summary.** The same `PreExistingSummary` feeds:

- An English **"Repository baseline"** section appended to the review prompt
  (`PromptBuilder.AppendBaseline`) — context for the LLM ("this is already in the
  repo, don't re-report it, but a new occurrence of a known weakness class in the
  diff still matters"), rendered right after the grounding findings.
- A German, **standalone** MR/PR comment (`PreExistingReport.Markdown`, posted via
  `IGitPlatform.PostNoteAsync` — a plain note, not part of the review) so a
  maintainer sees the repo's baseline without digging through the (English,
  redacted-for-the-LLM) prompt log. By default this comment is posted only on a
  PR/MR's **first** review (`FirstReviewOnly`), since the baseline does not change
  between pushes to the same PR.

Pre-existing findings **never** affect the merge verdict — the gate
(`docs/review-gate.md`) only ever evaluates the LLM's own findings, exactly as
before this feature existed.

### Configuration (`Naudit:Review:PreExisting`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `true` | Master switch for both the prompt section and the comment. `false` ⇒ neither (today's diff-only behavior). |
| `DetailSeverity` | `High` | Minimum severity listed individually (file + line); below it, findings are grouped by rule. |
| `MaxDetailed` | `50` | Cap on the individually-listed findings; anything beyond falls back into the rule groups instead of being dropped. |
| `MaxRules` | `30` | Cap on the number of rule groups shown. |
| `FirstReviewOnly` | `true` | Post the standalone comment only on a PR/MR's first review. `false` ⇒ every review. |

These five keys are DB-managed, same as the SAST keys above. `DetailSeverity`
has `AllowedValues` in the Settings catalog (`Info`/`Low`/`Medium`/`High`/`Critical`)
so an invalid value is rejected by `PUT /api/settings` instead of only surfacing
as recovery mode on the next restart.

**Why these keys live in two different places.** `MaxFindingsPerGroup` and
`MaxPreExistingPerGroup` belong to `SastOptions` (`src/Naudit.Infrastructure/Sast/`)
because they configure the reducer, an Infrastructure concern. `DetailSeverity`,
`MaxDetailed`, `MaxRules`, `FirstReviewOnly` and the shared `Enabled` switch belong
to `PreExistingOptions` on `ReviewOptions` (`src/Naudit.Core/Review/`) instead,
because `PreExistingSummary.Build` and the two renderers it feeds are called from
`ReviewService`, which lives in Core and — by the project's central rule — must
never reference `Naudit.Infrastructure` types. The split looks arbitrary from the
config-key names alone; it exists to keep Core's dependency direction intact.

## Prerequisites

The image must provide `opengrep` and `trivy` on `PATH` and the pinned rule sets
under `/opt/opengrep-rules` and `/opt/naudit-rules` (see `Dockerfile`). Naudit
clones via the platform token it already holds; no extra credentials needed.
