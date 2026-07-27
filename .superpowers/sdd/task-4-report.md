# Task 4 Report — DastProbeSession

## Status: DONE

Commit `56f89ab` on `feat/dast-probing`: "feat(dast): DastProbeSession — Playwright-MCP über
exec-stdio, kurzlebig je Review".

## What was implemented

- `src/Naudit.Infrastructure/Dast/DastProbeSession.cs`: `DastProbeSession.StartAsync(IDockerClient
  docker, DastOptions options, string probeContainer, ILoggerFactory loggerFactory,
  CancellationToken ct)` launches the Playwright-MCP server as a stdio process in the probe
  container via `docker.ExecStreamAsync` (Task 3), wraps the resulting `DockerExecStream` in a
  `ModelContextProtocol.Protocol.StreamClientTransport(serverInput: exec.Stdin, serverOutput:
  exec.Stdout, loggerFactory)`, connects `McpClient.CreateAsync(...)`, lists tools via
  `client.ListToolsAsync((RequestOptions?)null, ct)`, and exposes them as `IReadOnlyList<AITool>
  Tools`. `IAsyncDisposable.DisposeAsync` disposes the `McpClient` then the exec stream (`try/finally`).
- `tests/Naudit.Tests/DastProbeSessionTests.cs`: exactly the brief's test — `ThrowAfterExecDocker :
  FakeDockerClient` (empty `NextExecStdout`), asserts `StartAsync` throws and that the exec call was
  recorded with the right container name and `options.ProbeMcpArgv` before the throw.

## TDD

- RED: with only the test file present, `dotnet test --filter DastProbeSessionTests` failed with
  CS0103 (`DastProbeSession` doesn't exist in the current context — the compiler's phrasing for the
  missing-type case here, functionally the CS0246 the brief anticipated).
- GREEN: after adding `DastProbeSession.cs`, first compile attempt failed CS0246/CS1503 on
  `RequestOptions` — **SDK-surface adaptation**, see below. After adding `using ModelContextProtocol;`
  it compiled and the test passed.

## Handshake timeout — needed, and non-trivial

The brief assumed an empty `NextExecStdout` would make `McpClient.CreateAsync` fail fast
(EOF/throw). I verified empirically (throwaway repro project referencing
`ModelContextProtocol.Core 1.4.1` directly) that this assumption is **false** for the real SDK:
`McpClient.CreateAsync` over a `StreamClientTransport` backed by an empty `MemoryStream` does not
throw on EOF — it just hangs until cancelled. Confirmed via a standalone repro: with an 8s
`CancellationTokenSource` and no other timeout, it threw `TaskCanceledException` at exactly 8.02s,
i.e. it genuinely blocks until cancellation, not shorter.

So the timeout backstop described in the brief's Step 4 is **required**, not just defensive. I
implemented it as specified: a linked `CancellationTokenSource` (external `ct` + an internal
timeout) wraps `CreateAsync` + `ListToolsAsync`.

**Deviation from the brief's literal "e.g. 30s":** with a 30s timeout the test took the full 30s
(confirmed: `Duration: 30 s`), which conflicts with the explicit "keep the test fast / verify it
completes in seconds" constraint. I reduced the internal `HandshakeTimeout` constant to **10
seconds** — still generous for a warm probe container (image already pulled, container already
running `sleep infinity`; only `node` start + headless-Chromium launch remain, typically well under
that) while keeping the test at a `Duration: 10 s` instead of 30s. This is a production-relevant
value, not just a test knob — the constant lives in `DastProbeSession`, not in the test. Documented
with a German comment explaining both the "why a timeout at all" (SDK doesn't fail fast on EOF) and
the "why 10s" (probe container is already warm) reasoning.

Full-suite run confirmed no CI-speed regression: `Duration: 26 s` for all 705 tests, in line with
the pre-existing baseline scale.

## Files changed

- `src/Naudit.Infrastructure/Dast/DastProbeSession.cs` (new)
- `tests/Naudit.Tests/DastProbeSessionTests.cs` (new, verbatim per brief)

## SDK-surface adaptations vs. the brief's Step 3 code

1. **`using ModelContextProtocol;` added.** `RequestOptions` is declared in namespace
   `ModelContextProtocol`, not `ModelContextProtocol.Client` as the brief's interface note implied.
   Confirmed by reflecting the installed `ModelContextProtocol.Core 1.4.1` assembly
   (`ModelContextProtocol.RequestOptions`) and by cross-checking `McpClientToolConnector.cs`, which
   already has this exact `using` at the top for the same reason.
2. **`StreamClientTransport` ctor and `McpClient.CreateAsync`/`ListToolsAsync` overloads** — verified
   via reflection against the actual 1.4.1 DLL to match the brief exactly:
   `StreamClientTransport(Stream serverInput, Stream serverOutput, ILoggerFactory
   loggerFactory = default)`; `McpClient.CreateAsync(IClientTransport, McpClientOptions? = default,
   ILoggerFactory? = default, CancellationToken = default)`; `ListToolsAsync(RequestOptions,
   CancellationToken)` returning the auto-paginating list overload (not the raw
   `ListToolsRequestParams` overload) — no further code changes needed here.
3. **Timeout value 30s → 10s** — see above, driven by the CI-speed constraint plus empirically
   confirmed real SDK hang behavior.
4. **Defensive addition beyond the brief's literal code:** the brief's `catch` block only disposes
   `exec`, which would leak a successfully-created `McpClient` if `ListToolsAsync` throws after
   `CreateAsync` succeeded (a real code path the test doesn't exercise, since in the test
   `CreateAsync` itself is what times out — confirmed via the same standalone repro, which showed
   the hang/timeout happens inside `CreateAsync`, before `ListToolsAsync` is ever reached). Hardened
   by capturing `client` outside the `try` and disposing it (client-then-exec order, matching
   `DisposeAsync`) in the `catch` block if non-null. Re-verified green after this change.

## Self-review

- **Dispose ordering (client-then-exec):** `DisposeAsync` does `try { client.DisposeAsync() } finally
  { exec.DisposeAsync() }` — matches the brief. The `catch` block in `StartAsync` mirrors the same
  order for the partial-failure case (see adaptation #4).
- **Exec stream disposed on start failure:** yes, in all `StartAsync` failure paths (both the
  original CreateAsync-throws path and the newly hardened ListToolsAsync-throws-after-CreateAsync
  path).
- **No hang:** verified — without the timeout the test hung until manually timed out; with the 10s
  internal timeout it deterministically throws and the test passes in ~10s, full suite in line with
  baseline duration.
- `docker.ExecStreamAsync` call itself is outside the `try` (matches the brief) — if it throws
  (`DockerUnavailableException`, per `IDockerClient` contract), there is nothing to dispose yet.

## Full suite

`DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx` → **705 passed, 0 failed** (baseline
704 + 1 new test). No `GitWorkspaceProviderTests` flake observed on this run.

## Concerns

- The 10s handshake timeout is a judgment call, not something verified against a real Playwright-MCP
  container (no live Docker/MCP peer available in this sandbox). If real-world node+Chromium
  startup under load in the probe container occasionally exceeds 10s, the session will throw and the
  Task 5/6 analyzer's fail-open will simply skip DAST grounding for that review — not a correctness
  bug, but worth a quick sanity check against a real container once one is available, and the
  constant is trivially tunable if it proves too tight.
- `RequestOptions` type in the `ListToolsAsync` call is unrelated to the `Naudit:Review:*` `Options`
  pattern used elsewhere in the codebase; it's an MCP SDK type (nullable, passed as literal `null`)
  — no ambiguity risk found during compilation.

## Fix nach Review

**Observation:** The hardcoded 10-second timeout in `DastProbeSession.cs` (line 22) made the
`DastProbeSessionTests` test take ~10 seconds for no production reason. The test only verifies
that a timeout occurs on a broken MCP connection; this should be fast.

**Change:**

1. Added `HandshakeTimeout` property to `DastOptions.cs` (German doc comment explaining the
   config's purpose: allow tests to be fast while production remains tunable):
   ```csharp
   public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);
   ```

2. Removed the `private static readonly TimeSpan HandshakeTimeout` constant from
   `DastProbeSession.cs` and replaced its usage on line 42 with `options.HandshakeTimeout` in
   the `CancellationTokenSource` constructor. Kept the German comment explaining the timeout's
   rationale.

3. Updated `DastProbeSessionTests.cs` to construct `new DastOptions { HandshakeTimeout =
   TimeSpan.FromMilliseconds(200) }` instead of `new DastOptions()`, so the test now completes
   in ~0.2s instead of ~10s.

**Results:**

- **DastProbeSessionTests duration:** 10,000 ms → 258 ms (39x faster)
- **Full suite:** 705 passed, 0 failed, completed in 25 seconds (no regression)
- **Test assertions unchanged:** argv recorded + ThrowsAnyAsync still verified

**Commit:** `c33a8f0`
