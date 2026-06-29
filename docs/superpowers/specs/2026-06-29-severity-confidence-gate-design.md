# Severity/Confidence per inline finding + severity-aware gate — Design

*2026-06-29 · Naudit*

## Problem

The BA interim assessment ([[2026-06-24 Zwischenfazit naudit – nemotron-3-ultra-cloud]])
names two related weaknesses of the current gate:

- **#4 — Severity/Confidence only in the summary, not on the inline comments.** The
  verdict is binary; a *Critical* sits visually next to a nitpick.
- **#5 — The gate is not severity-aware.** A single low-confidence finding flips the
  fail-closed gate to `request_changes`, which risks blocking valid code (Automation
  Bias, the `.NET-10` hallucination being the worst case).

Today the LLM emits a free `verdict` (`approve` | `request_changes`) and a flat list of
`{file, line, comment}`. `ReviewService` trusts the verdict verbatim (fail-closed on an
unknown value). There is no per-finding severity, so the gate cannot distinguish a
blocking bug from a style remark.

## Goal

Make each LLM finding carry a **severity** and a **confidence**, surface them on the
inline comment, and **derive** the merge verdict from a configurable, severity-aware
policy: block only on a *confirmed* high-impact finding.

## Key decisions

- **Contract change — the LLM no longer returns a top-level `verdict`.** It returns
  `summary` + `comments[]`, where each comment now also has `severity`
  (`critical|high|medium|low|info`) and `confidence` (`high|medium|low`). The verdict is
  **computed by Naudit**, not asserted by the model. This directly removes the criticised
  binary verdict and makes the gate transparent and tunable.
- **Severity-aware gate policy (Core).** `verdict = request_changes` iff **any** review
  finding has `severity ≥ Gate.MinSeverity` **and** `confidence ≥ Gate.MinConfidence`.
  Defaults: `MinSeverity = High`, `MinConfidence = Medium` → "block only on a confirmed
  High/Critical", exactly recommendation #5. Both are config-tunable under
  `Naudit:Review:Gate`.
- **Reuse `FindingSeverity`** (`Info<Low<Medium<High<Critical`, already in Core and
  already ordered) for the comment severity — one severity scale across SAST and LLM.
  New `ReviewConfidence` enum (`Low<Medium<High`), ordered so `≥` works.
- **Bias toward *not* blocking on ambiguity.** A missing/unparseable severity → `Info`,
  missing/unparseable confidence → `Low` (both non-blocking). This is the deliberate
  inverse of the old fail-closed-on-unknown-verdict, and is the whole point of #5:
  don't block valid code on a weak signal. A structurally unparseable *response* still
  throws (real fail-closed remains for "no parseable answer at all").
- **Severity is visible on the comment.** Each inline comment body is prefixed with a
  typed badge `**🟠 High** · confidence medium` (emoji per severity). `InlineComment`
  gains `Severity`/`Confidence` fields (structured, for tests + future native rendering);
  the platform layer is unchanged (it still posts `Body`, which now carries the badge).
- **SAST stays grounding-only.** The gate is derived from the **LLM review findings**
  (inline *and* orphaned comments — both carry severity), never from SAST/SCA findings.
  This keeps the documented rule "the verdict stays LLM-only" intact; we only make that
  LLM verdict structured instead of a free string.
- **No new DI seam.** `Gate` is a nested property of the existing `ReviewOptions`
  (bound from `Naudit:Review`); no factory/registration change.

## Scope

- `ReviewService`: parse severity/confidence per comment, render the badge, compute the
  verdict from the gate policy, show the gate decision in the summary.
- `PromptBuilder.DefaultSystemPrompt`: drop `verdict`, require per-comment
  `severity`+`confidence`, explain the impact/certainty split.
- Core models: `ReviewConfidence`, `InlineComment.Severity/Confidence`, `ReviewGateOptions`.
- Docs (`configuration.md`, new `docs/review-gate.md`, `CLAUDE.md`) + appsettings defaults.

Out of scope: a two-pass detect→triage flow (BA #6), confidence calibration (#4 prose),
and SAST findings driving the gate — all deferred.

## Behaviour change

A merge that previously got `request_changes` from a single low-confidence remark now
**passes** unless there is a confirmed ≥High finding. Documented prominently; tunable via
`Naudit:Review:Gate:MinSeverity` / `:MinConfidence` (set `MinSeverity=Low` to restore a
near-always-blocking gate).

## References

- BA: [[2026-06-24 Zwischenfazit naudit – nemotron-3-ultra-cloud]] (recommendations #3, #5)
- Plan: `docs/superpowers/plans/2026-06-29-severity-confidence-gate.md`
- Docs: `docs/review-gate.md`
