## Task 7: Documentation

**Files:**
- Create: `docs/mcp-tools.md`
- Modify: `docs/configuration.md`

- [ ] **Step 1: Write `docs/mcp-tools.md`**

Create `docs/mcp-tools.md` (English, matching the other docs) covering:

- What it does: the review LLM can call MCP tools at review time; the first tool is Context7 (live library docs).
- The two provider paths (MEAI `ChatOptions.Tools` + function-invocation loop; ClaudeCode CLI `--mcp-config` + MCP-only `--allowedTools`), and that the built-in CLI file/shell tools stay off.
- Opt-in and fail-open behaviour (`Naudit:Review:Mcp:Enabled=false` ⇒ today's single-shot; unreachable server ⇒ tool-less review).
- The full config block (from the spec), including that the per-server `ApiKey` is env-only (list-shaped), while `Enabled`/`MaxIterations` are DB-manageable via Settings.
- The iteration cap (`MaxIterations`) and why it exists (token/latency).
- A pointer that Playwright/DAST are separate future slices (B/C), not part of this.

- [ ] **Step 2: Link it from `docs/configuration.md`**

Add a short "MCP tools (review runtime)" subsection to `docs/configuration.md` that summarizes the `Naudit:Review:Mcp:*` keys and links to `docs/mcp-tools.md`.

- [ ] **Step 3: Commit**

```bash
git add docs/mcp-tools.md docs/configuration.md
git commit -m "docs(mcp): MCP-Tools in der Review-Runtime + Konfiguration"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** Core-thin seam (Task 1), prompt guidance (Task 2), config + catalog (Task 3), fail-open/cache orchestration (Task 4), ClaudeCode CLI path with MCP-only allowlist (Task 5), real connector + function-invocation + iteration cap + conditional wiring (Task 6), docs (Task 7). Every spec section maps to a task.
- **Core rule:** only `IReviewToolProvider` (returning MEAI `AITool`) and `NullReviewToolProvider` live in Core; all `ModelContextProtocol.*` usage is in `Naudit.Infrastructure/Mcp/`. Verified: `Naudit.Core.csproj` references only `Microsoft.Extensions.AI.Abstractions`.
- **No behaviour change when off:** Task 1 keeps `ChatOptions.Tools` null with the null provider; Task 5 keeps the ClaudeCode args byte-identical (`--tools ""`, `--max-turns 1`) when MCP is off; Task 6 only wraps the client and swaps the provider when `Enabled && provider != ClaudeCode`.
- **Version-sensitive spots flagged:** MEAI `MaximumIterationsPerRequest`/`UseFunctionInvocation` and the MCP SDK `McpClient.CreateAsync`/`ListToolsAsync`/`HttpClientTransportOptions` are pinned to the verified package versions with an implementer note in Task 6.
