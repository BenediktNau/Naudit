# MCP tools in the review runtime (Context7) — Design

*2026-07-11 · Naudit*

## Problem

The review is single-shot: `IGitPlatform.GetChangesAsync` → grounding → `PromptBuilder.Build` →
one `IChatClient.GetResponseAsync` → post. The LLM reviews the diff against only what the prompt
carries. It has no way to fetch information it doesn't already know — most acutely, **up-to-date
library documentation**. When a diff uses an API from a fast-moving library, the model reviews it
against its training-cutoff knowledge and either misses real misuse or invents false positives
against an API shape that has since changed.

This is the first slice ("A") of a larger idea: make Naudit an **MCP client** so the review LLM
can call tools at review time. This slice wires the MCP plumbing and lands **one** tool —
[Context7](https://context7.com) (live library docs) — because it delivers value with **no running
target app**, and it de-risks the agentic-loop change for later dynamic slices (Playwright/DAST).

**Non-goals (this slice):**

- Any running-app tooling — Playwright, a browser, DAST. That needs the App-Runner (slice B) and
  is explicitly out of scope here.
- A general tool marketplace / arbitrary user-supplied tool registry. This slice ships the Context7
  seam and one configured server list; more servers are config, not code.
- Post-LLM filtering or tool-driven verdicts. Tool output is grounding only; the verdict stays
  LLM-driven through the existing severity/confidence gate (`docs/review-gate.md`).

## Decomposition context

The full idea (raised 2026-07-11) is three independent subsystems, each its own spec → plan → build:

- **A — MCP client in Naudit (this spec).** Naudit becomes an MCP client; the review becomes an
  agentic loop; Context7 is the first tool. Needs no running app.
- **B — App-Runner.** Checkout → build & run the changed code (Dockerfile/compose contract) →
  healthcheck → URL → teardown, sandboxed. The foundation for anything dynamic. Largest, riskiest.
- **C — Dynamic security testing** on top of B: LLM-drives-Playwright exploratory and/or a real
  scanner (ZAP/Nuclei) as a new `ISastAnalyzer`-style grounding source.

Slice A is deliberately first: smallest, no App-Runner risk, proves the loop plumbing.

## Decisions (settled during brainstorming, 2026-07-11)

1. **MCP lives in the review runtime**, not just the dev environment. Naudit itself becomes the MCP
   client; the review LLM calls tools.
2. **Context7 first, no running app.** Live library docs are the first and only tool this slice
   ships. Playwright/DAST wait for the App-Runner.
3. **Core-thin tool seam (approach ①).** A new Core abstraction returns MEAI `AITool`s;
   Infrastructure builds them from MCP. Same pattern as `IPromptRedactor` / `IContextCollector`.
   The two rejected alternatives — a decorating `IChatClient` that mutates the caller's
   `ChatOptions`, and moving the whole review call behind a Core `IReviewEngine` — were dropped as
   hacky and over-engineered respectively.
4. **Both provider paths in scope.** MEAI-tool providers (OpenAICompatible, Ollama, Anthropic-SDK)
   **and** the ClaudeCode CLI. They share one config source and diverge only in how the server list
   reaches the model.
5. **Opt-in, fail-open.** Off by default (`Naudit:Review:Mcp:Enabled=false`) ⇒ byte-identical
   single-shot as today. An unreachable server or a tool error degrades to a tool-less review, the
   same way a failed SAST checkout degrades to diff-only.
6. **Iteration cap.** The agentic loop is token- and latency-expensive; both paths cap the number
   of tool round-trips.

## Architecture

### The Core rule and where tools plug in

Core may depend only on `Microsoft.Extensions.AI.Abstractions`. The `ModelContextProtocol` SDK is a
concrete dependency and belongs in Infrastructure. The key enabling fact: **`AITool` / `AIFunction`
are MEAI *abstractions*** — Core may hold and pass them. Only the *building* of tools from MCP
servers happens in Infrastructure.

New Core abstraction (in `Naudit.Core.Abstractions`):

```csharp
public interface IReviewToolProvider
{
    // Returns the tools to offer the LLM for this review (empty when MCP is off/unreachable).
    Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default);
}
```

`ReviewService` sets `chatOptions.Tools` from it before the LLM call, then runs unchanged:

```csharp
var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
var tools = await _toolProvider.GetToolsAsync(request, ct);   // [] when off/unreachable
if (tools.Count > 0)
    chatOptions.Tools = [.. tools];
var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
```

The default (MCP off) returns `[]`, `chatOptions.Tools` stays null, and the request is identical to
today's single-shot. Core never references `ModelContextProtocol`.

### Two provider paths, one config source

Both read the same `Naudit:Review:Mcp` server list and diverge only in delivery to the model:

| | MEAI providers (OpenAICompatible, Ollama, Anthropic-SDK) | ClaudeCode CLI |
|---|---|---|
| Tool delivery | `chatOptions.Tools` via `McpReviewToolProvider` (`McpClientTool : AIFunction`) | `--mcp-config <json>` |
| Loop | `UseFunctionInvocation()` middleware, `MaximumIterationsPerRequest` cap | `--max-turns N` (was `1`) |
| Tool allowance | implicit (tools are in the request) | `--allowedTools "mcp__context7__resolve-library-id" "mcp__context7__get-library-docs"` — **and nothing else** |

### MEAI-provider path

- **`McpReviewToolProvider`** (Infra, `src/Naudit.Infrastructure/Mcp/`) implements `IReviewToolProvider`.
  It builds one `IMcpClient` per configured server via the `ModelContextProtocol` SDK
  (`McpClientFactory.CreateAsync`), lists tools (`await client.ListToolsAsync()` →
  `IList<McpClientTool>`, which derive from `AIFunction`), and returns them. Context7 uses the
  **remote HTTP transport** (`SseClientTransport` / streamable HTTP) — no Node subprocess in the
  container image. Clients and tool lists are **cached** for the process (server host is fixed; the
  tool catalog is stable), with a short TTL so a restart of the remote server recovers.
- **Function-invocation pipeline.** `AiClientFactory.Create(...)` stays, but its result is wrapped:
  `client.AsBuilder().UseFunctionInvocation().Build()`, configured with a `MaximumIterationsPerRequest`
  cap. This is applied only for the MEAI providers (the ClaudeCode client is its own `IChatClient`
  that doesn't honor `ChatOptions.Tools`, so wrapping it with function-invocation would be a no-op —
  see below).

### ClaudeCode CLI path

`ClaudeCodeChatClient` today deliberately disables everything agentic:
`"-p", "--output-format", "json", "--max-turns", "1", "--tools", ""`. The new path, **only when MCP
is enabled**, changes three things and nothing else:

1. **Register the servers:** add `--mcp-config <json>` built from the same `Naudit:Review:Mcp`
   config (server name → transport/url/headers). The Context7 key is injected as a header here.
2. **Allowlist only the MCP tools:** replace `--tools ""` with
   `--allowedTools "mcp__<server>__<tool>" …` naming exactly the MCP tools. **The built-in
   file/shell tools (Bash, Edit, Read, Write) stay off.** This is the security crux of the CLI path
   — a review bot must not gain host shell/file access over untrusted diff context. When MCP is off,
   the CLI args are byte-identical to today (`--tools ""`, `--max-turns 1`).
3. **Raise the turn cap:** `--max-turns N` (same cap value as the MEAI path) so the agent can call
   the tool and then answer.

The CLI produces its final assistant message (the review JSON) in the envelope `result` exactly as
today; `StripJsonFences` and the empty-result fail-closed check are unchanged. Envelope `usage`
already reports cumulative tokens across turns, so the audit token counts stay correct.

To feed the CLI the raw server list (it wants `--mcp-config`, not `AITool`s), the ClaudeCode client
receives the parsed `McpOptions` by injection — it does **not** go through `IReviewToolProvider`.
`IReviewToolProvider` for the ClaudeCode provider therefore returns `[]` (the tools travel via CLI
args, not `ChatOptions.Tools`); the branch is chosen in DI by the active `Naudit:Ai:Provider`.

### Prompt guidance

`PromptBuilder` gains a short, conditional block (rendered only when tools are present) telling the
model a live-docs tool exists and when to reach for it — e.g. *"You can look up current
documentation for a library via the available tool; use it when the diff uses an API you are unsure
about, rather than guessing against possibly-outdated knowledge. Do not use it for well-known
stdlib/basics."* Without guidance the model either never calls the tool or calls it constantly;
both waste the budget.

### Configuration

New section under the existing `Naudit:Review`:

```
Naudit:Review:Mcp:Enabled              = false            # opt-in master switch
Naudit:Review:Mcp:MaxIterations        = 4                # tool round-trip cap (both paths)
Naudit:Review:Mcp:Servers:0:Name       = context7
Naudit:Review:Mcp:Servers:0:Transport  = http            # http | stdio
Naudit:Review:Mcp:Servers:0:Url        = https://mcp.context7.com/mcp
Naudit:Review:Mcp:Servers:0:ApiKey     = <secret>        # Context7 key (optional; higher limits)
```

- The `ApiKey` is a secret → added to `SettingsCatalog` (`SettingDefinition`, `IsSecret: true`),
  DP-encrypted like other secrets. The server **list** is list-shaped, so — following the existing
  `ProjectTokens` / `Ui:Admins` precedent — it stays env/appsettings-shaped rather than a single
  DB-managed scalar; only the scalar switches (`Enabled`, `MaxIterations`) and the per-server
  `ApiKey` secret are DB-managed catalog entries.
- `Enabled=false` ⇒ `McpReviewToolProvider` returns `[]`, no pipeline wrapping takes effect on the
  request, CLI args unchanged ⇒ identical behaviour to today.

### Failure handling (fail-open, everywhere)

- **Server unreachable at startup/first use:** `McpReviewToolProvider` catches, logs, returns `[]`.
  The review runs tool-less. (Mirrors the failed-SAST-checkout → diff-only degrade.)
- **Tool call fails mid-review:** the function-invocation middleware surfaces the error to the model
  as a tool result; the model proceeds. The review is never failed by a tool error.
- **CLI path, `--mcp-config` server down:** the CLI simply won't have the tool; the model answers
  without it. If the CLI itself errors, the existing non-zero-exit handling applies (unchanged).
- **Iteration cap hit:** the loop stops and the model is asked for its final answer; a review is
  always produced.

### Component boundaries

- `IReviewToolProvider` (Core) — *what tools to offer*; returns MEAI abstractions only.
- `McpReviewToolProvider` (Infra) — *builds tools from MCP servers* (MEAI-provider path).
- `McpOptions` (Infra) — parsed `Naudit:Review:Mcp`; consumed by both `McpReviewToolProvider` and
  `ClaudeCodeChatClient`.
- `ClaudeCodeChatClient` (Infra) — *CLI-native MCP*; consumes `McpOptions`, builds `--mcp-config` +
  `--allowedTools`.
- `AiClientFactory` / DI — wraps MEAI clients with `UseFunctionInvocation`; leaves ClaudeCode as-is;
  registers the right `IReviewToolProvider` per provider.
- `PromptBuilder` (Core) — conditional tool-guidance block.
- `ReviewService` (Core) — sets `chatOptions.Tools` from the provider; otherwise unchanged.

## Testing approach

- **Core, no network.** `ReviewService`: a fake `IReviewToolProvider` returning `[]` proves the
  single-shot path is untouched; returning a fake `AITool` proves `chatOptions.Tools` is populated.
  `PromptBuilder`: the guidance block appears only when tools are present.
- **MEAI path.** `McpReviewToolProvider` against a stub MCP endpoint (or a fake transport) —
  asserts tools are listed and returned; asserts unreachable-server ⇒ `[]` (fail-open). The
  function-invocation loop and iteration cap are exercised with a `FakeChatClient` that emits a
  tool call then a final JSON message, asserting the tool ran and the cap bounds the loop.
- **CLI path.** `ClaudeCodeChatClient` with the existing `StubHttpMessageHandler`-style
  `IProcessRunner` fake: assert that MCP-off yields today's exact args (`--tools ""`,
  `--max-turns 1`, no `--mcp-config`), and MCP-on yields `--mcp-config`, an `--allowedTools`
  allowlist naming **only** the MCP tools (no Bash/Edit/Read), and the raised `--max-turns`.
- **Config gate.** `Enabled=false` end-to-end ⇒ identical request/args to pre-feature.
- **Manual/E2E.** A real Context7 call against a real provider is verified manually (as with the
  existing end-to-end path), not in CI.

## Open follow-ups (explicitly deferred)

- ClaudeCode CLI multi-turn cost/observability tuning (the loop can be chatty).
- Per-project MCP server lists (global-only here).
- Slice B (App-Runner) and slice C (Playwright/DAST) — separate specs.
