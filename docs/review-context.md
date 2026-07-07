# Review context enrichment

Naudit gives the reviewing LLM more than the raw diff: it cuts **surrounding code**,
**call-sites of changed symbols**, and a **repository overview** from the shallow
checkout it already makes (the same one the SAST analyzers use) and appends them to the
prompt as a read-only "Repository context" section. Nothing is installed in the target
repository — the context is derived per review and thrown away with the checkout.

## What it collects

1. **Surrounding code** — for each changed file: the whole file if it is at most
   `FullFileMaxLines` lines; otherwise the enclosing block around each changed hunk
   (indentation heuristic, with a `BlockPadLines` fallback window).
2. **Usages of changed symbols** — symbol names declared on added (`+`) diff lines
   (functions, types, C-family signatures) searched across the checkout, up to
   `MaxUsagesPerSymbol` call-sites with `UsageSnippetLines` of surrounding context each.
   The search skips the declaring files, vendor/build directories, and non-code noise
   (diffs, patches, lockfiles, and prose like `.md`/`.rst`/`.txt`) so the budget is spent
   on real call-sites, not on files that merely *mention* the symbol.
3. **Repository overview** — a directory tree (depth ≤ `MaxTreeDepth`) plus the first
   `ReadmeMaxLines` of a root `README.*`.

The section is assembled in priority order **surrounding code → usages → overview** and
truncated at `MaxChars` (the overrun point is marked `… [truncated by budget]`).

## Configuration

All keys live under `Naudit:Review:Context` (defaults in parentheses):

| Key | Meaning |
| --- | --- |
| `Enabled` (`true`) | Build the context section at all. `false` ⇒ today's diff-only prompt. |
| `MaxChars` (`40000`) | Character budget for the whole context section. |
| `FullFileMaxLines` (`400`) | File at most this many lines ⇒ whole file; larger ⇒ block excerpts. |
| `BlockPadLines` (`30`) | ± fallback window around a hunk anchor when the block heuristic is too tight. |
| `UsageSnippetLines` (`3`) | ± lines around each call-site. |
| `MaxUsagesPerSymbol` (`5`) | Cap on call-sites per symbol. |
| `MaxTreeDepth` (`3`) | Directory-tree depth in the overview. |
| `ReadmeMaxLines` (`50`) | README head length in the overview. |

## Cost note

Because context needs the checkout, enabling it means **one shallow clone per review**
even when SAST is off. Set `Naudit:Review:Context:Enabled=false` to restore the exact
diff-only behaviour and prompt.

## Design & limits

The extraction is deliberately language-agnostic (regex + indentation), tuned for
precision over recall: a missed symbol is fine, prompt spam is not. Everything collected
passes through the prompt redactor before reaching the LLM, exactly like the diff,
findings, and title. Precise parsers (Roslyn/tree-sitter) and a cached architecture
"repo map" are possible later stages behind the same `IContextCollector` seam. See
`docs/superpowers/specs/2026-07-07-review-context-enrichment-design.md`.
