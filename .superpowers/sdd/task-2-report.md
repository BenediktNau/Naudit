# Task 2 report: DastOptions — probing knobs

## What was implemented

Added two new options to `src/Naudit.Infrastructure/Dast/DastOptions.cs`, placed right after the
existing `ProbeImage` field and before `AppliesTo` (grouped with the other probing-related fields,
matching existing style — XML doc comment above each property):

- `MaxProbeSteps` (`int`, default `12`) — cap on the agentic probing loop (tool calls + model turns
  combined). Comment explains: token-frugal, dynamic checking is grounding, not an exhaustive scan.
- `ProbeMcpArgv` (`List<string>`, default `{ "node", "/app/cli.js", "--headless", "--browser",
  "chromium", "--no-sandbox" }`) — the exec argv that launches the stdio Playwright-MCP server
  inside the probe container (`docker exec`; no `--port` ⇒ stdio). List-shaped so it stays
  env-/appsettings-overridable, matching the convention already used by `Projects` in this file.

Both doc comments are in German, matching file style.

## TDD evidence

**RED** — command:
```
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Defaults_probingKnobs"
```
Result: `CS1061` — `'DastOptions' does not contain a definition for 'MaxProbeSteps'` (and same for
`ProbeMcpArgv`), confirming the new test exercises members that don't exist yet.

**GREEN** — command:
```
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastOptionsTests
```
Result: `Passed! - Failed: 0, Passed: 7, Skipped: 0, Total: 7, Duration: 39 ms`

**Full suite** — command:
```
DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx
```
Result: `Passed! - Failed: 0, Passed: 702, Skipped: 0, Total: 702, Duration: 26 s`
(baseline 701 + 1 new test = 702, exactly as expected). No flaky `GitWorkspaceProviderTests`
failure was observed — clean single run, no rerun needed.

## Files changed

- `src/Naudit.Infrastructure/Dast/DastOptions.cs` — +9 lines (`MaxProbeSteps`, `ProbeMcpArgv`).
- `tests/Naudit.Tests/DastOptionsTests.cs` — +11 lines (`Defaults_probingKnobs` fact).

## Self-review

- Diff matches the brief's Step 1/Step 3 code blocks verbatim.
- Placement is consistent with existing style: doc comment above each property, grouped with the
  other probing-adjacent fields (`ProbeImage`), directly before the `AppliesTo` method.
- `List<string>` satisfies the test's `IReadOnlyList<string>`-shaped equality assertion (the test
  compares against a plain array via `Assert.Equal`, which works against `List<string>`).
- No other files needed touching — `AppliesTo` logic and all other fields are untouched; both new
  fields are pure config knobs with no consumers yet (wired up by later PR-2 tasks).
- Confirmed only the two intended files were staged and committed (`git add` named them
  explicitly, not `-A`). Pre-existing unrelated uncommitted changes in the working tree
  (`.superpowers/sdd/progress.md`, `task-1-brief.md`, `task-1-report.md`, and an untracked plan
  doc under `docs/superpowers/plans/`) were left untouched, as they predate this task and are out
  of its scope.

## Concerns

None. Scope is minimal and self-contained; the brief's code matched the file's actual current
content exactly, so no adaptation was needed.

## Commit

`80e44a9` — `feat(dast): DastOptions.MaxProbeSteps + ProbeMcpArgv (stdio-MCP-Start)`
