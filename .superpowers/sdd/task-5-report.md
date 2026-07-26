# Task 5 Report — DastAnalyzer: orchestration happy path

## Implemented

- `src/Naudit.Infrastructure/Dast/DastProbePrompt.cs` — `DastProbePrompt.System(appUrl, maxSteps)`,
  exactly as specified in the brief (probing system prompt + strict JSON contract
  `{"findings":[{severity,endpoint,summary}]}`).
- `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs` — `DastAnalyzer : ISastAnalyzer`
  (`Name => "dast"`). Allowlist-gates via `DastOptions.AppliesTo(workspace.ProjectId)` before
  touching the runner; runs `IAppRunner.RunAsync` (`await using`), opens `DastProbeSession`
  (or uses `probeToolsOverride` test seam), wraps the global `IChatClient` in
  `UseFunctionInvocation` (capped at `MaxProbeSteps`) only when tools exist, runs the bounded
  agentic loop, parses the model's JSON into `ScanFinding(Tool:"dast", Category:Dast, …,
  Message:"{summary} ({endpoint})", FilePath:null)`. Fail-open on any internal exception;
  `OperationCanceledException` tied to the caller's token rethrows. Session and app teardown
  both happen in `finally`/`await using`, guaranteed on every path.
- `tests/Naudit.Tests/DastAnalyzerTests.cs` — the brief's two tests verbatim.
- `tests/Naudit.Tests/Fakes/FakeAppRunner.cs` — `IAppRunner` fake (`RunCalled`, `Disposed`,
  `ReturnNull`), `RunningApp` constructed exactly per PR-1 signature.

## TDD

- RED: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests` failed
  with CS0246 (`DastAnalyzer` not found, both call sites) before the source files existed.
- GREEN: same filter passed 2/2 after adding `DastProbePrompt.cs` + `DastAnalyzer.cs`.
- Full suite: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx` → **707 passed, 0
  failed** (baseline 705 + 2 new tests, matching the task instructions' expected count; the
  brief's own "Expected: PASS — 706" text is stale relative to the current baseline and was
  superseded by the task instructions). No `GitWorkspaceProviderTests` flake observed, no rerun
  needed.

## Signature adaptations

None needed — every real signature (`ISastAnalyzer`, `ScanFinding`, `FindingSeverity`,
`FindingCategory.Dast`, `IAppRunner`/`RunningApp` ctor, `DastProbeSession.StartAsync`,
`DastOptions.MaxProbeSteps`/`AppliesTo`, `FakeChatClient(string)`, `FakeDockerClient`,
`IReviewWorkspace`, and the `UseFunctionInvocation(loggerFactory, c => c.MaximumIterationsPerRequest
= …)` pattern from `DependencyInjection.cs`) matched the brief's code verbatim. Copied the
brief's `DastAnalyzer.cs` and `DastProbePrompt.cs` as given, no changes required.

## Self-review

- **Teardown on every path:** `app` is `await using` (disposes even if the inner try throws
  before reaching the `finally`); `session` disposal is in an explicit `finally` inside the
  nested try, covering the probe-loop and parse steps. Verified by the first test's
  `Assert.True(app.Disposed)`.
- **Cancellation vs. fail-open:** `catch (OperationCanceledException) when
  (ct.IsCancellationRequested)` rethrows only when the *caller's* token fired (not an internal
  timeout on some other linked token), matching the "only caller-cancellation rethrows" rule;
  everything else funnels into the generic `catch (Exception)` fail-open branch.
- **JSON tolerance:** `ParseFindings` catches `JsonException` only (deliberately, per the
  brief) and returns `[]`; a non-JSON model reply is treated as "no findings", not a fail-closed
  abort, matching the doc-string and the module's grounding-not-verdict role.
- **Allowlist gate ordering:** `AppliesTo` is checked before any runner/session/chat call, so
  the second test's `Assert.False(app.RunCalled)` holds without a race.

## Concerns

None. The commit contains exactly the four intended files — `git status` was checked before
staging and confirmed the pre-existing, unrelated working-tree modifications under
`.superpowers/sdd/*` (from prior/parallel task work, not touched by this task) and the untracked
`docs/superpowers/plans/2026-07-25-dast-probing-analyzer.md` were left out of the commit by
staging the explicit path list rather than `git add -A`.

## Commit

`d5a9449` — `feat(dast): DastAnalyzer — Runner + Probing-Loop + JSON→ScanFinding(Dast)`
(4 files changed, 217 insertions: `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`,
`src/Naudit.Infrastructure/Dast/DastProbePrompt.cs`, `tests/Naudit.Tests/DastAnalyzerTests.cs`,
`tests/Naudit.Tests/Fakes/FakeAppRunner.cs`).
