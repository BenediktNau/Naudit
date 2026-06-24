# SAST/SCA grounding

Naudit can clone the MR/PR head and run static-analysis (SAST) and dependency
(SCA) scanners, then feed the normalized findings into the review prompt as
**grounding**. The LLM still produces the single verdict — tools never block on
their own (no hard tool gate).

## Configuration (`Naudit:Sast`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch. `false` ⇒ exact diff-only behavior. |
| `Analyzers` | `["semgrep","trivy"]` | Active analyzers by name. |
| `Reducer` | `deterministic` | Finding de-duplication strategy (seam for a future `llm` reducer). |
| `AnalyzerTimeout` | `00:05:00` | Per-tool timeout. |
| `MaxFindingsPerGroup` | `20` | Cap per category after sorting. |

## Analyzers

- **semgrep** — SAST, multi-language, no build (does not execute repo code).
- **trivy** — SCA/dependency CVEs, multi-ecosystem, no build.
- **dotnet-sca** — `.NET` SCA via `dotnet list package --vulnerable`. **Opt-in:**
  it runs `dotnet restore`, which **executes the reviewed code's build logic**,
  and it needs the .NET SDK in the image (the default runtime image only ships
  semgrep + trivy). Enable only for trusted repos and an SDK-based image.

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

The image must provide `semgrep` and `trivy` on `PATH` (see `Dockerfile`). Naudit
clones via the platform token it already holds; no extra credentials needed.
