# Task 7 report: DI wiring + settings

## Status: Done

Commit: `1a10665` — `feat(dast): DastAnalyzer als ISastAnalyzer verdrahtet + MaxProbeSteps im Katalog`

## What was implemented

1. `src/Naudit.Infrastructure/DependencyInjection.cs`: inside the existing `if (dastOptions.Enabled)`
   block (after the `IAppRunner`/`DastOrphanSweeper` registrations from PR 1), added
   `services.AddScoped<ISastAnalyzer>(sp => new DastAnalyzer(...))`, resolving `IAppRunner`,
   `dastOptions` (closure), `IChatClient`, `IDockerClient`, `ILoggerFactory` — matching the brief's
   code exactly, with the same German comment. `using Naudit.Core.Abstractions;` and
   `using Microsoft.Extensions.AI;` were already present in the file — no using changes needed.
2. `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`: added
   `new("Naudit:Review:Dast:MaxProbeSteps", false),` alongside the other `Naudit:Review:Dast:*`
   entries (after `ProbeImage`). Only this one key was added, per the brief — `ProbeMcpArgv` and
   `HandshakeTimeout` are intentionally not in the catalog (list-shaped / not requested by the brief).
3. `tests/Naudit.Tests/DastWiringTests.cs`: appended the two tests from the brief verbatim
   (`Dast_enabled_registersDastAnalyzer_amongSastAnalyzers`, `Dast_disabled_registersNoDastAnalyzer`).

## TDD

- RED: added the two tests first, ran
  `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~Dast_enabled_registersDastAnalyzer|FullyQualifiedName~Dast_disabled_registersNoDastAnalyzer"`.
  `Dast_enabled_registersDastAnalyzer_amongSastAnalyzers` failed as expected
  (`Assert.Contains() Failure: Filter not matched in collection` — `Collection: []`, since no
  `ISastAnalyzer` was registered yet). `Dast_disabled_registersNoDastAnalyzer` trivially passed
  even pre-wiring (nothing registered ⇒ `DoesNotContain` holds), which is expected and still a
  meaningful regression guard once the analyzer exists.
- GREEN: after wiring + catalog key, `dotnet test ... --filter DastWiringTests` → 5/5 passed
  (3 pre-existing + 2 new).

## Config adaptations for the test

None needed. The brief flagged a risk that `GetServices<ISastAnalyzer>()` might throw if
`IChatClient` isn't resolvable and the test's `BaseSettings()` might need AI config added. Checked
`AiOptions.Provider` default (`AiProvider.Ollama`) and `AiClientFactory.Create`: the Ollama branch
requires no API key and only constructs an `OllamaApiClient` with an `HttpClient` (no eager network
call, no exception at construction time). `IChatClient` is registered as a lazy `AddSingleton`
factory, so it constructs fine under the existing `BaseSettings()` (`Naudit:Git:Platform=GitLab`,
`Naudit:GitLab:BaseUrl=...`) with no AI section set at all. No test bootstrap changes beyond the
two new `[Fact]`s.

## Full suite

`DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`:
- First run: 712 passed, 1 failed — `DockerAppRunnerTests.Run_appNeverBecomesHealthy_returnsNull_andTearsDownEverything`
  (`Assert.Equal() Failure: HashSets differ — Expected: [mcr.microsoft.com/playwright/mcp:latest], Actual: []`).
  Unrelated to this task's files (no DAST-analyzer-registration or catalog code touched by that
  test) and not in the file set this task modifies.
- Re-ran that test alone: passed (9/9 in `DockerAppRunnerTests`), confirming it's flaky under full
  parallel-suite contention, not a real regression — same flake category the brief called out for
  `GitWorkspaceProviderTests`, just a different test class this time.
- Full suite re-run: **713/713 passed** (baseline 711 + 2 new tests = 713, matches expectation).

## Files changed (committed)

- `src/Naudit.Infrastructure/DependencyInjection.cs`
- `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- `tests/Naudit.Tests/DastWiringTests.cs`

Not committed (per instructions): `.superpowers/sdd/*`, `docs/superpowers/plans/2026-07-25-dast-probing-analyzer.md`
(pre-existing dirty/untracked files unrelated to this task, left as found).

## Self-review

- Registration is inside the correct `if (dastOptions.Enabled)` block, placed after the
  `IAppRunner`/`DastOrphanSweeper` registrations from PR 1, matching the brief's snippet and
  comment verbatim.
- Both wiring tests are meaningful: the "enabled" test exercises the full DI graph resolution
  (constructs `DastAnalyzer` via `GetServices`, which requires `IAppRunner`, `IChatClient`,
  `IDockerClient`, `ILoggerFactory` to all resolve) and checks `Name == "dast"`; the "disabled"
  test guards against a future regression where the registration accidentally moves outside the
  `if` block.
- `AddScoped` matches the brief and is consistent with other per-review-scoped registrations
  (`IAiClientRouter` for Author/RoundRobin) — `ISastAnalyzer` instances are resolved once per
  review scope in `ReviewService`.
- Catalog change is additive, single key, no reordering of unrelated entries.

## Concerns

- None blocking. The one observed full-suite flake (`DockerAppRunnerTests`) is pre-existing and
  unrelated to this task's diff; worth a separate look if it recurs, but out of scope here.
