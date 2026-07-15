# Task 1 Report: Core tool seam + ReviewService wiring

## Summary

Implemented `IReviewToolProvider` (Core abstraction returning MEAI `AITool`s) and wired it into
`ReviewService.ReviewAsync` so that, with the default `NullReviewToolProvider` (returns `[]`),
`ChatOptions.Tools` stays `null` — behavior is byte-identical to before this change. Followed the
brief's steps verbatim (TDD: RED → GREEN → commit).

## What was implemented

- **Created** `src/Naudit.Core/Abstractions/IReviewToolProvider.cs`: interface
  `Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest, CancellationToken)` plus the default
  `NullReviewToolProvider` (returns `[]`). Only depends on `Microsoft.Extensions.AI` (`AITool`)
  and `Naudit.Core.Models` — Core rule intact (verified `dotnet build` of `Naudit.Core.csproj`
  alone succeeds, no new package references added).
- **Modified** `src/Naudit.Core/Review/ReviewService.cs`: primary ctor gained a trailing
  `IReviewToolProvider toolProvider` parameter (after `auditSink`). In `ReviewAsync`, right before
  the `chatClient.GetResponseAsync` call, tools are fetched via `toolProvider.GetToolsAsync(request, ct)`
  and only assigned to `chatOptions.Tools` when non-empty (`tools.Count > 0`) — so the no-op default
  never touches `ChatOptions.Tools`, leaving it `null`.
- **Modified** `src/Naudit.Infrastructure/DependencyInjection.cs`: registers
  `services.AddSingleton<IReviewToolProvider>(new NullReviewToolProvider());` immediately after
  `services.AddSingleton(aiOptions);` (as specified). A later task (Task 6) will swap this for the
  real MCP-backed provider when MCP + a MEAI-compatible provider is active.
- **Modified** `tests/Naudit.Tests/Fakes/FakeChatClient.cs`: added `LastOptions` capture.
- **Created** `tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs`: fixed-tool-list fake for tests.
- **Modified** `tests/Naudit.Tests/ReviewServiceTests.cs`: `CreateService` helper gained a
  `toolProvider` parameter (defaults to `NullReviewToolProvider`); added the two brief-specified
  tests `ReviewAsync_withoutToolProvider_leavesChatOptionsToolsNull` and
  `ReviewAsync_withTools_populatesChatOptionsTools`.
- **Modified** `tests/Naudit.Tests/ReviewAuditSinkTests.cs` (not in the brief's file list, but a
  necessary consequence of the ctor signature change — see "Deviation from brief" below).

## TDD evidence

### RED

Command: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ReviewServiceTests"`

Run after Steps 1–3 (fakes + tests written, Core seam NOT yet created):

```
/home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7/tests/Naudit.Tests/ReviewServiceTests.cs(21,9): error CS0246: The type or namespace name 'IReviewToolProvider' could not be found (are you missing a using directive or an assembly reference?) [.../Naudit.Tests.csproj]
/home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7/tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs(8,71): error CS0246: The type or namespace name 'IReviewToolProvider' could not be found (are you missing a using directive or an assembly reference?) [.../Naudit.Tests.csproj]
```

Matches the brief's expectation exactly: "BUILD FAIL — `IReviewToolProvider` / `NullReviewToolProvider` do not exist yet."

### GREEN

After Steps 5–7 (Core seam created, `ReviewService` wired, DI registered) plus the extra
`ReviewAuditSinkTests.cs` ctor-call fix (see below):

Command: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ReviewServiceTests"`

```
Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 77 ms - Naudit.Tests.dll (net10.0)
```

(27 pre-existing `ReviewServiceTests` + the 2 new ones, all pass.)

Full-suite run, command: `dotnet test Naudit.slnx`

```
Passed!  - Failed:     0, Passed:   345, Skipped:     0, Total:   345, Duration: 4 s - Naudit.Tests.dll (net10.0)
```

(One transient environment-level failure was observed on an earlier full-suite run —
`ExternalAuthTests.GitHubChallenge_notMapped_whenDisabled` threw
`System.IO.IOException: The configured user limit (128) on the number of inotify instances has
been reached...` from `WebApplicationFactory`/`FileSystemWatcher` startup, unrelated to any code
change here. Confirmed environmental: (1) `dotnet test --filter "FullyQualifiedName~ExternalAuthTests"`
passed in isolation immediately after, and (2) a clean re-run of the full suite passed 345/345 with
zero failures. Not caused by this change.)

## Files changed

- `src/Naudit.Core/Abstractions/IReviewToolProvider.cs` (new)
- `src/Naudit.Core/Review/ReviewService.cs`
- `src/Naudit.Infrastructure/DependencyInjection.cs`
- `tests/Naudit.Tests/Fakes/FakeChatClient.cs`
- `tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs` (new)
- `tests/Naudit.Tests/ReviewServiceTests.cs`
- `tests/Naudit.Tests/ReviewAuditSinkTests.cs` (not in brief — see below)

## Self-review findings

- Verified `chatOptions.Tools` stays `null` for the default no-op path (explicit test assertion
  `Assert.Null(chat.LastOptions!.Tools)`), and gets populated (`[.. tools]`, a fresh array) when a
  provider returns a non-empty list.
- Verified Core's dependency footprint is unchanged: `src/Naudit.Core/Naudit.Core.csproj` still
  references only `Microsoft.Extensions.AI.Abstractions`; built `Naudit.Core.csproj` alone
  successfully with no new package references.
- Checked for any other direct `ReviewService` constructor call sites across the repo
  (`grep -rln "ReviewService"` over all `.cs` files, excluding `bin`/`obj`) to make sure nothing
  else silently broke or was missed. Found exactly one such site beyond the brief's list —
  `ReviewAuditSinkTests.cs` — and fixed it (see Deviation below). `SastWiringTests.cs` resolves
  `ReviewService` via DI (`AddNauditInfrastructure`), so it picked up the new `NullReviewToolProvider`
  registration automatically and needed no change.
- DI registration placement matches the brief exactly (right after
  `services.AddSingleton(aiOptions);`), and uses `IReviewToolProvider` which is already in scope via
  the existing `using Naudit.Core.Abstractions;` in `DependencyInjection.cs`.

## Deviation from brief

The brief's file list did not include `tests/Naudit.Tests/ReviewAuditSinkTests.cs`, but it has its
own local `CreateService` helper that directly calls `new ReviewService(...)` with a positional
9-argument list (one short of the new 10-parameter primary ctor after this change). Once the ctor
gained the trailing `IReviewToolProvider toolProvider` parameter (Step 6), that file failed to
compile (`CS7036`, missing required parameter). This wasn't a case of the brief being wrong — it
was simply an incomplete file list, since `ReviewAuditSinkTests` isn't part of the brief's explicit
"Files" section. I fixed it consistently with the brief's own established pattern: appended
`new NullReviewToolProvider()` as the last constructor argument. No test logic changed — same
behavior as before (no-op tool provider), only the ctor call updated. All 3 tests in that file still
pass. This was small, unambiguous, and clearly implied by the ctor-shape change the brief itself
mandates, so I did not stop to ask.

## Concerns

None. Change is minimal and additive; default behavior is provably unchanged (dedicated test
asserts `Tools` stays `null`); Core rule intact; full suite green.
