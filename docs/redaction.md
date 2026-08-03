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
| `secret` | `password=`/`passphrase=`/`secret=`/`credential=`/`api_key=`/`token=` assignments (only the value — the key stays readable), including prefixed keys like `DB_PASSWORD=` or `x-token:`; plus high-entropy tokens |
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
- **Short secrets are the keyword rule's job, not the entropy pass's.** At the
  default threshold of 4.0 bits/char a token shorter than 16 chars can never
  reach it (`log2(16) = 4.0`, and only with every character distinct), so the
  entropy pass is blind there by construction. The keyword rule therefore accepts
  prefixed keys (`DB_PASSWORD=`, `MY_DB_CREDENTIAL=`, `x-token:`) — `_` and `-`
  do not count as word characters on its left boundary, while a letter or digit
  still blocks it so identifiers like `authToken` are not matched.
- **The entropy pass stops at `=`.** A token ends before an assignment's `=`
  (trailing `=`/`==` is still consumed as base64 padding), so `KEY=value` is
  weighed as the *value alone*, never as key-plus-value. Two reasons, both learned
  the hard way: gluing the key on mixes two alphabets and inflates the entropy
  over the threshold, and masking the pair destroys the key name — the very
  context a reader needs to tell a secret from a harmless pin. A public 40-char
  commit SHA (3.62 bits/char, below the threshold) used to be masked purely
  because `OPENGREP_RULES_REF=` sat in front of it (4.44).
- **Quality cost.** A masked value is less context for the model; the typed
  placeholder mitigates this.
- **Out of scope (for now):** names and broad PII (NER / Microsoft Presidio).
  The `IPromptRedactor` seam is designed so a Presidio- or LLM-backed redactor
  can be plugged in later without touching `Naudit.Core`.

## Extending

Implement `Naudit.Core.Abstractions.IPromptRedactor` in
`src/Naudit.Infrastructure/Redaction/`, then select it in `DependencyInjection`.
Core stays MEAI-only (the interface lives in Core; implementations in Infrastructure).
