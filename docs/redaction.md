# Prompt redaction

Naudit sends the merge-request diff (plus static-analysis grounding and the MR
title) to an LLM. When the configured AI provider is a hosted service
(`Anthropic`, `OpenAICompatible`, `ClaudeCode`), that content leaves your
infrastructure. To avoid leaking credentials and personal data, Naudit masks
sensitive values **before** the prompt is built and sent.

Redaction runs entirely in-process — no external tool, no network — and is
**independent of the SAST/SCA feature** (it works even with `Naudit:Sast:Enabled=false`).

## What gets masked

The default `PatternRedactor` masks, replacing each hit with a typed placeholder
`«redacted:<kind>»`:

| Kind | Examples |
| --- | --- |
| `token` | AWS access keys (`AKIA…`), GitHub PATs (`ghp_…`, `github_pat_…`), Slack tokens (`xox…`), JWTs (`eyJ….….…`) |
| `private-key` | PEM private-key blocks (`-----BEGIN … PRIVATE KEY-----`; the base64 body is caught by the entropy pass) |
| `secret` | `password=`/`secret=`/`api_key=`/`token=` assignments (only the value), and high-entropy tokens |
| `ip` | IPv4 addresses (octet-validated) and full-form IPv6 |
| `email` | e-mail addresses |

The typed placeholder is deliberate: the model still sees *that* a secret/IP was
present (which actually helps it flag hard-coded secrets), without seeing the value.

Redaction is **line-preserving**: it never adds or removes lines and leaves diff
structure lines (`@@`, `+++`, `---`, `diff --git`, `index`) untouched, so inline
comment positions stay correct.

## Configuration

```jsonc
"Naudit": {
  "Redaction": {
    "Enabled": true,            // default ON; false ⇒ no-op (previous behaviour)
    "EntropyThreshold": 4.0,    // Shannon bits/char for the high-entropy fallback
    "MinEntropyTokenLength": 20 // only token-like substrings this long are entropy-checked
  }
}
```

> **Default ON for all providers.** This is a behaviour change versus earlier
> versions: by default the diff is now redacted before it reaches the LLM. Set
> `Naudit:Redaction:Enabled=false` to disable (e.g. when using a fully local
> Ollama and you want maximum review context).

## Trade-offs & limits

- **Heuristic.** Expect occasional false negatives (unusual secret formats) and
  false positives (a long hash flagged as `secret`). The entropy pass only fires
  on long tokens that mix letters **and** digits, which keeps normal identifiers
  and version numbers safe; thresholds are tunable above.
- **Quality cost.** A masked value is less context for the model; the typed
  placeholder mitigates this.
- **Out of scope (for now):** names and broad PII (NER / Microsoft Presidio).
  The `IPromptRedactor` seam is designed so a Presidio- or LLM-backed redactor
  can be plugged in later without touching `Naudit.Core`.

## Extending

Implement `Naudit.Core.Abstractions.IPromptRedactor` in
`src/Naudit.Infrastructure/Redaction/`, then select it in `DependencyInjection`.
Core stays MEAI-only (the interface lives in Core; implementations in Infrastructure).
