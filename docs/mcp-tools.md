# MCP tools in the review runtime

Naudit is now an **MCP client**: the reviewing LLM can call tools at review time, not just read
the diff and static-analysis grounding it's handed. The first (and currently only) tool wired in
is [Context7](https://context7.com) — live, up-to-date library documentation. When a diff uses an
API from a fast-moving library, the model would otherwise judge it against training-cutoff
knowledge alone and either miss real misuse or invent false positives against an API shape that
has since changed. Context7 needs **no running target app**, which is why it's the first tool: it
delivers value immediately and de-risks the agentic-loop plumbing for future, app-dependent tools.

> Playwright/browser tooling and dynamic security testing (DAST) are separate, **future** slices
> (they need a sandboxed App-Runner to build and run the changed code first). This feature is the
> MCP-client plumbing plus Context7 only — no running-app tooling ships here.

## Two provider paths, one config source

Both paths read the same `Naudit:Review:Mcp` server list; they only differ in how the tools reach
the model:

| | MEAI providers (OpenAICompatible, Ollama, Anthropic-SDK) | ClaudeCode CLI |
| --- | --- | --- |
| Tool delivery | `ChatOptions.Tools`, built by `McpReviewToolProvider` (`src/Naudit.Infrastructure/Mcp/`) from the MCP SDK's `McpClientTool : AIFunction` | `--mcp-config <path>` — the server list as JSON, written to a **0600 temp file** (never argv, since it can carry the `ApiKey`) and deleted again after the CLI run |
| Loop | `IChatClient.AsBuilder().UseFunctionInvocation()`, capped by `MaximumIterationsPerRequest` | `--max-turns N` (today's single-shot uses `1`) |
| Tool allowance | implicit — whatever tools are in the request | `--allowedTools "mcp__<server>"` per configured server, **and nothing else** |

For the MEAI providers, `AiClientFactory`'s client is wrapped with the MEAI function-invocation
middleware only when MCP is enabled; `Naudit.Core`'s `IReviewToolProvider` (the only new Core
abstraction — it returns MEAI `AITool`s, so Core stays MEAI-only) supplies the tool list via
`McpReviewToolProvider`, which fans out to every configured server and aggregates their tools.

For the ClaudeCode CLI, the tools don't travel through `ChatOptions.Tools` at all — the CLI is
its own `IChatClient` that ignores it. Instead `ClaudeCodeChatClient` is handed the same
`McpOptions` directly and builds `--mcp-config` + `--allowedTools` itself. Critically, the
allowlist names **only** the configured MCP servers (`mcp__<server>`) — the CLI's built-in
file/shell tools (Bash, Edit, Read, Write) stay off in both the MCP-on and MCP-off case. A review
bot must not gain host shell/file access from untrusted diff context. Each configured server
`Name` is validated against `^[A-Za-z0-9_-]+$` before it's used to build the allowlist string, so
a stray space or special character in the config can't smuggle extra tokens into it. When MCP is
disabled, the CLI args are byte-identical to before this feature (`--tools ""`, `--max-turns 1`).

## Opt-in and fail-open

- **Opt-in.** `Naudit:Review:Mcp:Enabled=false` is the default: `chatOptions.Tools` stays null (no
  MEAI wrapping happens), the CLI args are unchanged, and the review is exactly today's
  byte-identical single-shot.
- **New egress.** Enabling MCP opens an outbound channel this self-hosted bot didn't have before:
  once tools are on, the model sends library/API identifiers derived from the diff to the
  configured MCP server (e.g. `context7.com`) to resolve documentation. Opt in only once you're
  comfortable with that data leaving your deployment.
- **Fail-open.** A configured server that's unreachable (startup or first use) is caught, logged,
  and dropped — the review runs **tool-less**, the same way a failed SAST checkout degrades to a
  diff-only review. A successful tool list is cached for the process lifetime (server host is
  fixed, the tool catalog is stable); an empty result is **not** cached, so the next review retries
  a server that has since come back up.
- **Tool errors mid-review** (MEAI path) surface to the model as a tool result via the
  function-invocation middleware; the model proceeds and a review is still produced.

## Configuration

```jsonc
"Naudit": {
  "Review": {
    "Mcp": {
      "Enabled": false,          // opt-in master switch — default off, byte-identical single-shot
      "MaxIterations": 4,        // tool round-trip cap per review, both provider paths
      "Servers": [
        {
          "Name": "context7",              // ^[A-Za-z0-9_-]+$ — used verbatim in the CLI allowlist
          "Transport": "http",             // "http" | "stdio"
          "Url": "https://mcp.context7.com/mcp",  // http transport
          "ApiKey": "YOUR_CONTEXT7_KEY"     // optional secret; sent as an Authorization: Bearer header
          // "Command": "npx",              // stdio transport instead of Url
          // "Arguments": ["-y", "some-mcp-server"]
        }
      ]
    }
  }
}
```

- `Enabled` and `MaxIterations` are plain scalars in `SettingsCatalog` — **DB-manageable** via the
  WebUI Settings page like most other `Naudit:*` keys (see
  [Configuration › Where configuration lives](configuration.md#where-configuration-lives)).
- `Servers` is a **list**, so — following the `ProjectTokens`/`Ui:Admins` precedent — it stays
  env-var/appsettings-configurable rather than a DB-managed catalog entry. The per-server
  `ApiKey`, though, is a **secret**: set it via user-secrets, an environment variable, or your
  deployment's secret management — never in `appsettings.json` (same rule as every other secret
  in this project, see [Configuration › Where configuration lives](configuration.md#where-configuration-lives)).
- Context7's exact expected auth header may evolve; `ApiKey`, when set, is sent as
  `Authorization: Bearer <ApiKey>` on the http transport (and merged into the `--mcp-config` JSON
  as a `headers` entry for the CLI path). Context7 also works without a key at a lower rate limit.

## Iteration cap

`MaxIterations` bounds how many tool round-trips a single review may make (MEAI:
`MaximumIterationsPerRequest`; CLI: `--max-turns`) before the model is asked for its final answer.
The agentic loop is token- and latency-expensive compared to today's one-shot call, so both paths
share the same cap — a review always completes, it just stops calling tools once the budget is
spent.

## Extending

The seam is `Naudit.Core.Abstractions.IReviewToolProvider` (returns MEAI `AITool`s; the default
`NullReviewToolProvider` returns none, keeping non-MCP deployments untouched). The MCP-specific
wiring — `McpOptions`, `IMcpToolConnector`/`McpClientToolConnector` (the real `ModelContextProtocol`
SDK connection), and `McpReviewToolProvider` — lives entirely in
`src/Naudit.Infrastructure/Mcp/`; Core never references the `ModelContextProtocol` package. A
future tool (or a future App-Runner-backed Playwright/DAST slice) is another server entry plus, if
it needs more than MCP gives for free, another `IReviewToolProvider`/`IMcpToolConnector`
implementation — not a change to `Naudit.Core`.
