# Task 4 report: `McpReviewToolProvider` orchestration (fail-open + cache)

## What I implemented

Followed the brief verbatim (TDD: fake connector + failing tests → verify RED → implement →
verify GREEN → full suite → commit).

1. `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs` — the per-server seam interface
   (`ConnectAndListAsync(McpServerConfig, CancellationToken) → Task<IReadOnlyList<AITool>>`).
2. `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs` — test double with `Returns(server, tools...)`
   / `Throws(server)` per-server-name configuration and a `CallCount`.
3. `tests/Naudit.Tests/McpReviewToolProviderTests.cs` — 4 tests: disabled→empty (connector never
   called), aggregate across servers, failing-server-skipped (fail-open), cache across two
   `GetToolsAsync` calls (connector called once).
4. `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs` — implements Core's
   `IReviewToolProvider`. `Enabled=false` or no servers ⇒ `[]`, connector never touched.
   Otherwise iterates `options.Servers`, calls the connector per server inside a try/catch that
   swallows (and logs a warning for) any exception unless cancellation was requested, aggregates
   into one flat `List<AITool>`. Double-checked locking via a `SemaphoreSlim(1,1)` gate: only a
   non-empty result populates `_cached`, so an all-servers-down first attempt is not cached and a
   later review retries.

All four files match the brief's code blocks byte-for-byte (I copy-pasted from the brief, then
verified against the actual `IReviewToolProvider`, `McpOptions`/`McpServerConfig`, and
`ReviewRequest` signatures already on this branch — no adjustments were needed, everything lined
up).

## TDD evidence

**RED** — before `McpReviewToolProvider.cs` existed:

```
$ cd /home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7 && \
  dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"
...
/home/.../tests/Naudit.Tests/McpReviewToolProviderTests.cs(17,20): error CS0246:
The type or namespace name 'McpReviewToolProvider' could not be found
(are you missing a using directive or an assembly reference?)
[/home/.../tests/Naudit.Tests/Naudit.Tests.csproj]
```

Build failure as expected — the type didn't exist yet.

**GREEN** — after implementing `McpReviewToolProvider.cs`:

```
$ dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"
...
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 37 ms - Naudit.Tests.dll (net10.0)
```

All four tests (`Disabled_returnsEmpty_andNeverCallsConnector`,
`Aggregates_toolsFromAllServers`, `FailingServer_isSkipped_othersStillReturn`,
`Caches_nonEmptyResult_acrossCalls`) pass.

**Full suite (parallel, default):**

```
$ dotnet test Naudit.slnx
...
Failed! - Failed: 18, Passed: 335, Skipped: 0, Total: 353, Duration: 3 s
```

All 18 failures were `WebApplicationFactory`/host tests
(`AdminEndpointTests`, `WebhookEndpointTests`, `ExternalAuthTests`, etc.) throwing
`System.IO.IOException: The configured user limit (128) on the number of inotify instances has
been reached` from `PhysicalFilesWatcher` during host bootstrap — the documented sandbox flake,
unrelated to this change (no file I ran touches file watchers or those endpoints).

**Full suite (single-threaded, to confirm environmental):**

```
$ dotnet test Naudit.slnx -- xUnit.MaxParallelThreads=1
...
Passed!  - Failed: 0, Passed: 353, Skipped: 0, Total: 353, Duration: 10 s
```

0 failures single-threaded — confirms the 18 parallel failures were the known inotify flake, not
real regressions.

## Files changed

- `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs` (new)
- `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs` (new)
- `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs` (new)
- `tests/Naudit.Tests/McpReviewToolProviderTests.cs` (new)

Commit: `ebd6380 feat(mcp): McpReviewToolProvider — Server aggregieren, fail-open, cachen`
(exact message from the brief's Step 6).

## Self-review

- Code matches the brief exactly; no deviations were needed since the consumed types
  (`IReviewToolProvider`, `McpOptions`, `McpServerConfig`, `ReviewRequest`) already exist on this
  branch (Tasks 1 & 3) with the signatures the brief assumes.
- Core rule intact: `McpReviewToolProvider` lives in `Naudit.Infrastructure/Mcp/`, only
  implements the Core interface `IReviewToolProvider`; no Core file touched.
- Fail-open behaviour verified by a dedicated test (`FailingServer_isSkipped_othersStillReturn`) —
  a throwing connector for one server doesn't stop the loop or propagate; the exception filter
  `when (!ct.IsCancellationRequested)` deliberately lets a caller-requested cancellation still
  propagate as `OperationCanceledException` rather than being swallowed and logged as a warning.
- Caching: double-checked locking with `SemaphoreSlim(1,1)` avoids a redundant connector round-trip
  under concurrent first calls; only non-empty results are cached, matching the "retry on next
  review if all servers were down" requirement. This is process-lifetime (not per-request) caching,
  as specified — `McpReviewToolProvider` needs to be registered as a singleton in DI (Task 5, not
  in scope here) for the cache to actually span reviews.
- No `ModelContextProtocol` package added — confirmed no new package reference was introduced
  (`git diff` on `*.csproj` files is empty for this commit).

## Concerns

None. The four tests are exactly the ones prescribed, the implementation is the verbatim code
from the brief, and the only test failures seen were the pre-documented sandbox inotify flake,
confirmed environmental by the single-threaded re-run.
