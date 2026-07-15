# Task 7 report — Documentation (MCP tools / Context7)

## What I did

1. Read the task brief (`.superpowers/sdd/task-7-brief.md`) and the two reference docs
   (`docs/redaction.md`, `docs/review-context.md`) plus `docs/configuration.md` for house style.
2. Verified ground truth against the actual merged code (not just the design doc, which the brief
   flagged may have drifted) before writing anything:
   - `src/Naudit.Core/Abstractions/IReviewToolProvider.cs` — the Core seam + `NullReviewToolProvider`.
   - `src/Naudit.Infrastructure/Mcp/McpOptions.cs` — `McpOptions`/`McpServerConfig` shape
     (`Enabled`, `MaxIterations` default `4`, `Servers` list with `Name`/`Transport`/`Url`/
     `Command`/`Arguments`/`ApiKey`).
   - `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs` — fail-open aggregation, cache-only-
     on-success behaviour.
   - `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs` — real MCP SDK connector, http
     (`Authorization: Bearer`) vs stdio transport.
   - `src/Naudit.Infrastructure/DependencyInjection.cs` (lines ~44-76) — the actual wiring:
     `UseFunctionInvocation` wrap only for MEAI providers when `Mcp.Enabled`, `IReviewToolProvider`
     swapped between `McpReviewToolProvider` and `NullReviewToolProvider`.
   - `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs` — the CLI path: `--mcp-config`
     written to a 0600 temp file (deleted in `finally`), `--allowedTools "mcp__<server>"` per server
     (confirmed: server-name-only allowlist, **not** per-tool as an earlier design draft suggested),
     server-name validated against `^[A-Za-z0-9_-]+$`, byte-identical off-args (`--tools ""`,
     `--max-turns 1`).
   - `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (lines 36-37) — confirmed only
     `Naudit:Review:Mcp:Enabled` and `:MaxIterations` are catalog entries; no `ApiKey` entry, so the
     whole `Servers` list stays env/appsettings-only, exactly as the brief's ground truth states
     (this also confirms the *design* doc's plan to add `ApiKey` to the catalog was **not** what
     shipped — I documented the shipped behaviour, not the design draft).
   - `src/Naudit.Core/Review/PromtBuilder.cs` (`AppendToolGuidance`) — the conditional prompt block.
3. Wrote `docs/mcp-tools.md` (new file) covering: what it does / why Context7 first, the two
   provider-path table, opt-in + fail-open behaviour, the full config block (JSON, matching the
   `redaction.md`/`review-context.md` `jsonc` style), which keys are DB-managed vs. env-only, the
   iteration-cap rationale, and an "Extending" section naming the actual seam types. Explicitly
   scoped Playwright/DAST as separate future slices (B/C), not part of this doc's feature.
4. Added a "MCP tools (review runtime)" subsection to `docs/configuration.md`, placed after the
   GitHub App note and before "Choosing an AI provider" (keeps it near the `## Keys` table it
   supplements). Also added three new rows to the `## Keys` table itself
   (`Naudit:Review:Mcp:Enabled`, `:MaxIterations`, `:Servers:<n>:...`) so the table stays the single
   canonical key reference, each linking to `mcp-tools.md`.
5. Committed both files with the exact message from the brief.

## Files changed

- `docs/mcp-tools.md` (new, 105 lines)
- `docs/configuration.md` (+14 lines: 3 new key-table rows + new subsection)

Commit: `b0478eb` — `docs(mcp): MCP-Tools in der Review-Runtime + Konfiguration`

## Self-review

- **Accuracy vs. ground truth in the task prompt:** every bullet in the prompt's "ground truth"
  section is reflected — two provider paths/one config source, `--mcp-config` via 0600 temp file
  (not argv), `--allowedTools "mcp__<server>"` granting only MCP tools (built-in CLI tools off),
  opt-in default false, fail-open degrade, all four config keys (`Enabled`, `MaxIterations`,
  `Servers` with `Name`/`Transport`/`Url`/`Command`/`Arguments`/`ApiKey`), the DB-manageable vs.
  env-only split, and the Playwright/DAST future-slice pointer.
  - I did **not** overstate Context7's auth header — phrased as "sent as `Authorization: Bearer`"
    with a caveat that the exact expected header may evolve, per the prompt's caution.
  - I did **not** invent CLI flags beyond `-p`, `--output-format json`, `--max-turns`,
    `--mcp-config`, `--allowedTools`, `--model`, `--system-prompt`, `--tools ""` — all verified
    directly in `ClaudeCodeChatClient.cs`.
- **No placeholders.** No `TODO`/`TBD`/lorem-ipsum text anywhere in either file.
- **Links checked:** `docs/mcp-tools.md` → `https://context7.com` (external, informational) and
  `configuration.md#where-configuration-lives` (anchor verified present — `## Where configuration
  lives` header exists at line 3). `docs/configuration.md` → `mcp-tools.md` (three references, file
  exists at `docs/mcp-tools.md`, same directory, relative path correct — matches how the file links
  to `redaction.md`/`review-context.md`/`github-app.md` elsewhere).
- **Style match:** used the same `jsonc` fenced config block style as `redaction.md`, the same
  "Key | Meaning" table row style as the rest of `configuration.md`, English prose, no code changes.
- **Core-rule accuracy:** confirmed and stated that only `IReviewToolProvider`/
  `NullReviewToolProvider` live in Core, all `ModelContextProtocol` usage is Infrastructure-only —
  matches the brief's task-7 self-review notes and my own read of `Naudit.Core.csproj`'s dependency
  (not re-verified via csproj read here, but consistent with the CLAUDE.md architecture description
  and the file locations I did read).

## Concerns

- None blocking. One judgment call: I diverged from the *design spec's* stated plan (adding `ApiKey`
  to `SettingsCatalog`) and documented the *shipped* code instead (only `Enabled`/`MaxIterations` are
  catalog entries, confirmed by reading `SettingsCatalog.cs` directly) — this matches the task
  prompt's explicit ground truth, so I'm confident it's correct, but flagging the discrepancy from
  the design doc in case it's news.
