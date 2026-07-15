# Fix report — MCP feature open review findings (#54)

Branch `feat/mcp-context7`, worktree `.claude/worktrees/feat-mcp-context7`. Baseline: `e62cfb7`
("Final-Review-Härtung").

## Finding 1 — `BuildMcpConfigJson` doesn't validate `Command`/`Url`

**Status: fixed-now.** Not addressed by e62cfb7 (that commit hardened the temp-file/validator/DI
concerns, not server-field validation). Added fail-closed guards in `BuildMcpConfigJson`
(`ClaudeCodeChatClient.cs`): a stdio server with a blank `Command`, or an http server with a blank
`Url`, now throws `InvalidOperationException($"MCP-Server '{s.Name}': Command/Url fehlt (...)")`
before any JSON is built — mirrors the existing `IsValidServerName` fail-closed style. Also moved
the `BuildMcpConfigJson(mcp)` call to run *before* the temp file is created, so an invalid config
never creates a (leaked, empty) temp file in the first place — ties directly into Finding 4.

Tests: `GetResponseAsync_stdioServerWithoutCommand_throws_andLeavesNoTempFile`,
`GetResponseAsync_httpServerWithoutUrl_throws_andLeavesNoTempFile` (both also assert no
`naudit-mcp-*.json` file is left behind).

## Finding 2 — `MaxIterations` no lower bound

**Status: fixed-now.** Not addressed by e62cfb7. Clamped at both use sites with `Math.Max(1, ...)`:
CLI path (`ClaudeCodeChatClient.cs`, `--max-turns`) and MEAI path (`DependencyInjection.cs`,
`FunctionInvokingChatClient.MaximumIterationsPerRequest`). Comment explains why (0/negative would
disable tool rounds / break the CLI arg or the MEAI middleware).

Tests: `GetResponseAsync_mcpMaxIterationsZeroOrNegative_clampsToOne_inMaxTurnsArg` (CLI path);
`McpEnabled_meaiProvider_maxIterationsZeroOrNegative_clampsFunctionInvocationLoopToOne` (MEAI path,
resolves `IChatClient` from the real DI composition and inspects the
`FunctionInvokingChatClient` middleware via `GetService<T>()`).

## Finding 3 — `McpClient` never disposed

**Status: fixed-now**, via the "hold + dispose on shutdown" option from the finding's own menu —
judged least-invasive and correct for the existing singleton-provider lifetime. `McpClientToolConnector`
(the only place a real `McpClient` is created) now:
- keeps a `List<McpClient>` of every **successfully** connected client (guarded by a lock; the
  tools returned from `ConnectAndListAsync` call back through that client, so it must not be
  closed while cached tools are in use — matches the class's own "process lifetime" doc comment).
- if `ListToolsAsync` throws, the just-created client is **not** retained — it's disposed
  immediately in a `catch`, since `McpReviewToolProvider` retries a failing server on every
  subsequent review (an uncached client would otherwise leak one `McpClient`/process per retry).
- implements `IAsyncDisposable`: `DisposeAsync()` closes every held client, best-effort (one
  failure doesn't abort the rest, logged as a warning).

No interface change (`IMcpToolConnector` stays as-is; `FakeMcpToolConnector` untouched).
`McpClientToolConnector` is registered as a singleton via a factory
(`services.AddSingleton<IMcpToolConnector>(sp => new McpClientToolConnector(...))`); the built-in
`ServiceProvider` tracks and disposes any created instance that implements
`IDisposable`/`IAsyncDisposable` regardless of the compile-time service type it's registered
under — confirmed via a small reflection probe of `Microsoft.Extensions.AI`/
`ModelContextProtocol.Core` and by the fact that `dotnet test` now genuinely *needs* `await using`
(not `using`) on service providers that resolve this singleton (see below) — so no extra DI wiring
was required beyond the class change itself.

Side effect (expected, not a bug): a `ServiceProvider` holding this singleton can no longer be
disposed synchronously. Updated the two existing `McpDiCompositionTests` that resolve
`IReviewToolProvider`/`IChatClient` with MCP+MEAI enabled from `using var sp = ...` to
`await using var sp = ...` (the tests are `async Task` now) — this surfaced immediately as a test
failure and confirms the disposal wiring is real, not just added dead code.

Test: `McpClientToolConnectorTests` — asserts the type implements `IAsyncDisposable`, and that
`DisposeAsync()` with no connected clients completes cleanly. A full connect→dispose round-trip
against a real MCP server isn't unit-reachable (same reason the class doc says "NUR manuell E2E
getestet") — no fake/in-memory MCP transport exists in this codebase to exercise it without a real
process/network endpoint.

## Finding 4 — temp MCP-config file can leak if the write throws

**Status: fixed-now.** Wrapped the `FileStream`/`StreamWriter` create+write block in its own
`try { ... } catch { best-effort File.Delete(mcpConfigPath); throw; }`, inside the existing
`if (mcpEnabled)` block — the outer `try { RunAsync } finally { delete }` starts only afterwards,
so without this wrap a write failure right after `CreateNew` would skip cleanup entirely (the
outer `finally` was never reached). The 0600-atomic creation (`UnixCreateMode` on `FileStreamOptions`)
is untouched. Combined with Finding 1's reordering (validate before creating the file), the most
realistic real-world trigger of this — a bad server config — now can't leak a file at all; the
generic write-failure wrap (disk full, permission race, etc.) is defense in depth for cases that
aren't easily simulated in a unit test without a filesystem-fault seam (none exists in this
codebase), so no dedicated test for that exact sub-case — Findings 1's two tests exercise the
combined validate+cleanup path instead.

## Finding 5 — docs: `ApiKey` must not look appsettings-safe

**Status: fixed-now** (partially pre-fixed by e62cfb7, contradiction remained). e62cfb7 added an
egress-disclosure note but did **not** touch this wording. Found the actual problem in three
places that all used the phrase "env-var/appsettings-only" for the *entire* `Servers` list
including the per-server `ApiKey` — self-contradictory next to the immediately-following sentence
"never in `appsettings.json`" (`docs/mcp-tools.md`) and outright saying "appsettings-only" for the
secret in `docs/configuration.md` (two spots: the key table row and the "MCP tools" prose section).
Fixed by separating the two claims: the **server list** (`Name`/`Transport`/`Url`/`Command`/
`Arguments`) is legitimately env/appsettings-configurable (list-shaped, not DB-managed) — but the
per-server `ApiKey` is called out explicitly as a **secret** that must go via user-secrets/env/
secret-manager only, matching the project's standing secrets rule.

MD040 (fenced code block without a language): checked all three MCP-related docs
(`docs/mcp-tools.md`, `docs/superpowers/plans/2026-07-11-mcp-context7-review.md`,
`docs/superpowers/specs/2026-07-11-mcp-context7-review-design.md`). Found one: the spec doc's
"Configuration" section had an untyped ` ``` ` fence around a `Key = Value` listing — retagged
` ```text `. `mcp-tools.md` and the plan doc already tag every fence correctly (`jsonc`, `csharp`,
`bash`, etc.) — no change needed there.

## Test results

`dotnet test Naudit.slnx` (from the worktree): **367/367 passed**, 0 failed, 0 skipped (run
multiple times for confidence).

Observed once, non-reproducing: `GitWorkspaceProviderTests.CheckoutAsync_throwsAndCleansUp_whenGitFails`
failed on one run with a stray leftover directory from an unrelated, concurrently-running
`TestAppFactory`-based test (`Directory.CreateTempSubdirectory("naudit-test-db...")` — its name
happens to match that test's own overly-broad `"naudit-*"` temp-dir glob). Reproduced the exact
same flake on the **pristine `e62cfb7` baseline** (stashed all changes incl. untracked files,
re-ran) to confirm it is a pre-existing test-isolation race unrelated to this fix — passed cleanly
on the very next baseline run too, consistent with a timing-dependent parallel-collection race, not
a regression from this change. Confirmed the changes here don't contribute to it: the new/changed
temp-file activity in this fix uses a distinct `naudit-mcp-*.json` **file** name pattern, checked
via `Directory.GetFiles` in the new tests — it can't collide with that other test's
`Directory.GetDirectories("naudit-*")` glob. Left untouched — out of scope for #54.

## Files touched

- `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs` (Findings 1, 2, 4)
- `src/Naudit.Infrastructure/DependencyInjection.cs` (Finding 2)
- `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs` (Finding 3)
- `docs/mcp-tools.md`, `docs/configuration.md`,
  `docs/superpowers/specs/2026-07-11-mcp-context7-review-design.md` (Finding 5)
- `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs` (Findings 1, 2, 4 tests)
- `tests/Naudit.Tests/McpDiCompositionTests.cs` (Finding 2 MEAI-path test + `await using` fix from
  Finding 3's disposal change)
- `tests/Naudit.Tests/McpClientToolConnectorTests.cs` (new — Finding 3 disposal-seam test)
