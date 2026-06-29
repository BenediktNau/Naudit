# Plan: Severity/Confidence per finding + severity-aware gate (TDD)

Spec: `docs/superpowers/specs/2026-06-29-severity-confidence-gate-design.md`
Branch: `feat/severity-gate` (off `main`). One commit per task, red → green.

## Task 1 — Core models
- `ReviewConfidence { Low, Medium, High }` (ordered for `≥`).
- `InlineComment` gains `Severity` (`FindingSeverity`, default `Info`) and
  `Confidence` (`ReviewConfidence`, default `Low`) as trailing optional params.
- `ReviewOptions.Gate` → new `ReviewGateOptions { MinSeverity = High, MinConfidence = Medium }`.
- No standalone test (covered by ReviewService tests in Task 2).

## Task 2 — Severity-aware gate in ReviewService (red → green)
New/updated tests in `ReviewServiceTests`:
- confirmed High + high confidence → `RequestChanges`.
- Critical (any confidence ≥ Medium) → `RequestChanges`.
- High but **Low** confidence → `Approve` (below MinConfidence).
- Medium severity, high confidence → `Approve` (below MinSeverity).
- comment without severity/confidence → treated Info/Low → `Approve`.
- inline comment Body carries the severity badge; `InlineComment.Severity/Confidence` set.
- tunable: `Gate.MinSeverity = Critical` → a High finding no longer blocks.
- orphan (bad line) high finding still drives the gate.
- unparseable response (JSON `null`) still throws (fail-closed retained).
Update the 3 tests coupled to the old contract (`returnsRequestChanges_whenModelSaysSo`,
`validLine_isPostedInline`, `withUnknownVerdict_throws`).
Implementation: parse severity/confidence (lenient), render badge, compute verdict from
the gate policy over all accepted comments, surface the decision in the summary.

## Task 3 — Prompt contract
- `PromptBuilder.DefaultSystemPrompt`: drop `verdict`; require `severity`+`confidence`
  per comment; explain impact vs. certainty. Keep the toolchain-grounding sentence
  (`PromptBuilderTests.DefaultSystemPrompt_groundsToolchain` must stay green).

## Task 4 — Web endpoint test
- `ReviewEndpointTests.Review_withValidToken...`: feed a Critical/high comment so the
  derived verdict is `request_changes` (the endpoint code itself is unchanged).

## Task 5 — Config + docs
- `appsettings.json`: `Naudit:Review:Gate` defaults.
- `docs/configuration.md`: Gate keys. New `docs/review-gate.md`. `CLAUDE.md`: request-flow
  note (verdict now derived from a severity-aware policy; SAST still grounding-only).

## Task 6 — Vault
- Design note `2026-06-29 Severity-Confidence-Gate – Design.md`, board move, Architektur update.
