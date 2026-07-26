# Task 6 report: DastAnalyzer failure paths + guaranteed teardown

**Branch:** `feat/dast-probing`

## Summary

Appended the 4 failure-path tests from the brief (with the branch's own adaptations for the
probe-throws timing and caller-cancellation) to `DastAnalyzerTests.cs`. All 4 passed against the
Task 5 `DastAnalyzer` implementation **as-is** — no analyzer code change was needed. One minimal
fake change was required (see below) to make the caller-cancelled test exercise real
cancellation-propagation instead of trivially passing.

## Tests added — pass/fail on first run

| Test | Result on first run | Notes |
|---|---|---|
| `Analyze_appNeverStarts_returnsEmpty` | **Passed immediately** | `app is null ⇒ return []` path (line 35 of `DastAnalyzer.cs`) already covers this. 2 ms. |
| `Analyze_nonJsonModelOutput_returnsEmpty_andTearsDown` | **Passed immediately** | `ParseFindings` catches `JsonException` ⇒ `[]`; `await using var app` disposes on the way out regardless. 5 ms. |
| `Analyze_probeSessionThrows_returnsEmpty_andTearsDownApp` | **Passed immediately** (with the prescribed short `HandshakeTimeout`) | `probeToolsOverride: null` drives the real `DastProbeSession.StartAsync` against the empty `FakeDockerClient` (`ExecStreamAsync` returns an empty stdout stream). `McpClient.CreateAsync` hangs on the handshake until the internal `HandshakeTimeout`-linked CTS fires, throwing `OperationCanceledException`; since the *caller's* `ct` was never cancelled, `DastAnalyzer`'s `catch (OperationCanceledException) when (ct.IsCancellationRequested)` guard does **not** match, so it falls into the generic `catch (Exception)` ⇒ `[]`, and the outer `finally`/`await using` still tear down the app. Used `DastOptions.HandshakeTimeout = 200ms` (already a settable option from Task 4) instead of the default 10s so the test stays fast. **Duration: 247 ms** (well under the 2s ceiling). |
| `Analyze_callerCancelled_propagates` | **Needed a `FakeAppRunner` change** (test fake, not analyzer) | See below. |

## FakeAppRunner change (test infrastructure, not analyzer)

`FakeAppRunner.RunAsync` previously ignored the `CancellationToken` entirely, so a pre-cancelled
token passed straight through and the analyzer's `runner.RunAsync(workspace, ct)` call would never
throw — the test would then only be checking whatever `AnalyzeAsync` itself does with a token it
never actually observes anywhere. Per the task's explicit instruction, added:

```csharp
public Task<RunningApp?> RunAsync(IReviewWorkspace workspace, CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();   // echte Aufrufer-Abbruch-Propagation für den Cancel-Test
    RunCalled = true;
    ...
```

This makes the fake behave like a real cancellable I/O call (starting a container, polling health,
etc., all honour the token in the Task 1/5 implementation) and exercises `DastAnalyzer`'s
`catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` rethrow for real,
which is exactly the code path the test is meant to prove.

No `DastAnalyzer.cs` production code was touched — the Task 5 implementation already satisfies all
four failure paths (null app, non-JSON output, probe-session exception, caller cancellation) by
construction: `await using var app = ...` + the inner `try/finally` around the probe session
guarantee teardown runs on every non-happy path, and the two-tier `catch` (specific
`OperationCanceledException` rethrow vs. generic `Exception` swallow) already implements the
fail-open/caller-cancel distinction the tests assert.

## Files changed

- `tests/Naudit.Tests/DastAnalyzerTests.cs` — 4 new `[Fact]` tests appended.
- `tests/Naudit.Tests/Fakes/FakeAppRunner.cs` — one-line `ct.ThrowIfCancellationRequested()` added
  at the top of `RunAsync`.
- `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs` — **unchanged**.

## Verify evidence

```
$ dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests -l "console;verbosity=detailed"
  Passed Analyze_notAllowlisted_returnsEmpty_withoutRunning         [49 ms]
  Passed Analyze_mapsModelJson_toDastFindings                       [137 ms]
  Passed Analyze_appNeverStarts_returnsEmpty                        [2 ms]
  Passed Analyze_nonJsonModelOutput_returnsEmpty_andTearsDown       [5 ms]
  Passed Analyze_callerCancelled_propagates                         [5 ms]
  Passed Analyze_probeSessionThrows_returnsEmpty_andTearsDownApp    [247 ms]
  Passed: 6

$ DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx
  Passed!  - Failed: 0, Passed: 711, Skipped: 0, Total: 711, Duration: 56 s
```

Baseline was 707; 711 = 707 + 4 new tests. No flake on this run (`GitWorkspaceProviderTests`
did not need a rerun).

## Self-review

- All 4 new tests are meaningful (not tautological): each drives a distinct code path in
  `DastAnalyzer.AnalyzeAsync` and asserts both the return value (`[]`) and a teardown/propagation
  side effect (`app.RunCalled` / `app.Disposed` / exception type), matching the brief's exact
  assertions.
- The probe-throws test genuinely exercises the real `DastProbeSession.StartAsync` code path
  (`probeToolsOverride: null`), not a stub — confirmed via the 247 ms duration (handshake-timeout
  bound, not instant), i.e. it really went through the CTS-linked timeout and not some short-circuit.
- Caller-cancellation test now proves real propagation (via the `FakeAppRunner` fix) rather than
  passing vacuously.
- German comments added to both new/changed lines per repo convention.
- Full suite: 711/711 passed (baseline 707 + 4 new), no flake on this run.
- Scope discipline: only `tests/Naudit.Tests/DastAnalyzerTests.cs` and
  `tests/Naudit.Tests/Fakes/FakeAppRunner.cs` were staged/committed. Pre-existing unrelated working-tree
  changes (`.superpowers/sdd/*` docs, an untracked plan file under `docs/superpowers/plans/`) were left
  untouched and unstaged, per the binding constraints.

## Concerns

None. All four tests passed against the unmodified Task 5 `DastAnalyzer`; the only change was to
test infrastructure (`FakeAppRunner`), explicitly anticipated and authorized by the task's own
adaptation note.

## Status
DONE (not DONE_WITH_CONCERNS) — no production-code gap was found.
