# ClaudeCode provider (local `claude` CLI)

The `ClaudeCode` AI provider runs the review through the **locally installed `claude`
CLI** (Claude Code) in headless mode instead of calling an SDK/HTTP endpoint. Its purpose
is **subscription instead of API key**: the CLI authenticates with your logged-in Claude
account (Pro/Max), so reviews are not billed per token.

It is a plain provider swap behind the MEAI abstraction — the review is still a single
pass over the diff returning the same JSON. It is **not** an agentic review (no repository
access, no tools).

## Precondition

`claude` must be installed and authenticated **on the machine that runs Naudit** (this is
not installed or managed by Naudit):

```bash
# Install (see the Claude Code docs for your platform)
npm install -g @anthropic-ai/claude-code

# Headless auth for Pro/Max — produces a long-lived OAuth token
claude setup-token
export CLAUDE_CODE_OAUTH_TOKEN=<token-from-setup-token>
```

In a container the token is an environment variable (`CLAUDE_CODE_OAUTH_TOKEN`); Naudit
passes it through to the `claude` subprocess. Alternatively set `Naudit:Ai:ApiKey` — Naudit
forwards it as `CLAUDE_CODE_OAUTH_TOKEN` to the subprocess.

## Configuration

```bash
dotnet user-secrets set "Naudit:Ai:Provider" "ClaudeCode" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "sonnet"     --project src/Naudit.Web
```

`Naudit:Ai:Model` accepts an alias (`sonnet`, `opus`, `haiku`, `fable`) or a full model id;
empty defaults to `sonnet`. `Naudit:Ai:TimeoutSeconds` bounds the subprocess (default 600) — more precisely, the window from when the diff has been written to the CLI's stdin until the process exits.

## How it works

Naudit invokes `claude -p --output-format json --max-turns 1 --tools "" --model <model>
--system-prompt <prompt>` and pipes the annotated diff to **stdin**. It parses the JSON
envelope and uses its `result` field as the model output. Any non-zero exit, `is_error`,
non-`success` subtype, empty result, or timeout fails the review (fail-closed) — no comment
is posted.

The envelope's `usage` object (`input_tokens`/`output_tokens`) is mapped onto the MEAI
`ChatResponse.Usage`, so the review audit and dashboard count this provider's token usage
like the API provider does — even though a Pro/Max subscription is not billed per token. If a
run reports no `usage`, the counts stay null (no fabricated zero).

## Non-goals

- No Dockerfile changes here — `claude` is an environment precondition. Baking Node + the
  CLI into the deployed image is a separate, later step.
- No agentic review (no repo access / tools / MCP), no streaming, no multi-turn sessions.
- Future hardening option: `claude --json-schema` for schema-validated structured output.
