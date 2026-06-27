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
| `MaxFindingsPerGroup` | `20` | Cap per category after sorting. |

## Analyzers

- **opengrep** — SAST, multi-language, no build (does not execute repo code).
  OpenGrep is the fully-LGPL fork of Semgrep; we run it with **pinned, explicit
  rule paths** — never `--config auto` (that would pull the license-restricted
  Semgrep registry rules and send telemetry).
- **trivy** — SCA/dependency CVEs, multi-ecosystem, no build.
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

- All findings are included **repo-wide**, annotated `[in diff]` (touched by the
  MR) vs `[pre-existing]`.
- Findings are de-duplicated, sorted by severity then in-diff, and capped per
  category before grounding.
- Graceful degradation: a single analyzer failure is logged and skipped; a failed
  checkout degrades the review to diff-only (it does not fail the gate).
- The system prompt instructs the model to treat the toolchain/target framework
  as valid and current (mitigates outdated-knowledge false positives).

## Prerequisites

The image must provide `opengrep` and `trivy` on `PATH` and the pinned rule sets
under `/opt/opengrep-rules` and `/opt/naudit-rules` (see `Dockerfile`). Naudit
clones via the platform token it already holds; no extra credentials needed.
