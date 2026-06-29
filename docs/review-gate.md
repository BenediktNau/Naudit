# Review gate (severity-aware verdict)

Naudit returns a machine-readable verdict (`approve` | `request_changes`) so a CI/CD
job can gate the merge (see [CI integration](ci-integration.md)). The verdict is **not**
asserted by the LLM — it is **derived** from the per-finding severity and confidence the
model assigns to each comment.

## How a finding is rated

For every comment the LLM emits `severity` and `confidence`:

| Field | Values (ascending) | Meaning |
| --- | --- | --- |
| `severity` | `info` < `low` < `medium` < `high` < `critical` | impact if the issue is real |
| `confidence` | `low` < `medium` < `high` | how sure the model is the issue is real |

Each comment is posted with a typed badge so the rating is visible at the line, e.g.
`**🟠 High** · confidence medium`. A missing or unparseable `severity`/`confidence`
defaults to `info`/`low` — i.e. **non-blocking** (see below).

## The gate policy

```
verdict = request_changes  ⟺  some finding has
              severity   ≥ Naudit:Review:Gate:MinSeverity
          AND confidence ≥ Naudit:Review:Gate:MinConfidence
```

Defaults: `MinSeverity = High`, `MinConfidence = Medium` → **block only on a confirmed
High/Critical finding**. A single low-confidence remark or a `medium`/`low` nitpick no
longer flips the gate to `request_changes`. This deliberately biases toward *not* blocking
valid code (the inverse of a naive fail-closed gate that blocked on any single finding).

Both line-mapped (inline) and unmappable (summary "Findings ohne Position") comments count
toward the gate — both carry severity/confidence.

> Static-analysis / dependency (SAST/SCA) findings remain **grounding only** and never
> drive the verdict by themselves; they are context for the LLM, which then rates its own
> findings. See [SAST grounding](sast-grounding.md).

## Configuration

```jsonc
"Naudit": {
  "Review": {
    "Gate": {
      "MinSeverity": "High",     // Info | Low | Medium | High | Critical
      "MinConfidence": "Medium"  // Low | Medium | High
    }
  }
}
```

- **Stricter gate** (block on more): lower the thresholds, e.g. `MinSeverity = "Medium"`.
- **Near-always-blocking** (closest to the old behaviour): `MinSeverity = "Low"`,
  `MinConfidence = "Low"`.
- **Only the worst blocks:** `MinSeverity = "Critical"`, `MinConfidence = "High"`.

## Why

The gate change implements the BA interim assessment's recommendations #4 (severity/
confidence on the inline comments, not just the summary) and #5 (a severity-aware gate
that blocks only on a confirmed High/Critical, to avoid blocking valid code on a weak or
low-confidence signal — including the known `.NET-10` version hallucination).
