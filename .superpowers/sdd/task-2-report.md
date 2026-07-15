# Task 2 report: PromptBuilder tool-guidance block

## What was implemented

Exactly as specified in `task-2-brief.md`, no deviations from the "replace" blocks:

1. **`src/Naudit.Core/Review/PromtBuilder.cs`**
   - `Build` gained a new trailing optional parameter `bool toolsAvailable = false`.
   - Inside `Build`, after `AppendFindings(sb, findings ?? []);` and before `return`, added a call
     to the new `AppendToolGuidance(sb, toolsAvailable)`.
   - Added the private helper `AppendToolGuidance` (placed directly above `AppendCategory`, i.e.
     right after `AppendFindings`/`AppendCategory` region as the brief's "below `AppendFindings`"
     instruction implies): no-ops when `toolsAvailable` is false; otherwise appends a `# Tools
     available` section instructing the model to use the Context7 documentation tool only when
     unsure about an API, not for stdlib/trivial code, and to still return the required review JSON
     after any tool call.

2. **`src/Naudit.Core/Review/ReviewService.cs`**
   - Moved the `var tools = await toolProvider.GetToolsAsync(request, ct);` fetch to *before* the
     `PromptBuilder.Build` call (previously it came after).
   - `PromptBuilder.Build` now receives `toolsAvailable: tools.Count > 0` as its new trailing arg.
   - `chatOptions.Tools = [.. tools];` assignment (guarded by `tools.Count > 0`) now happens after
     `messages` is built — same condition and code as before, just reordered per the brief.
   - Updated the German comment on the tools block to also mention the prompt hint
     ("+ Hinweis im Prompt").

3. **`tests/Naudit.Tests/PromtBuilderTests.cs`**
   - Added `Build_withoutTools_hasNoToolGuidance` — asserts `"Tools available"` is absent when
     `toolsAvailable` defaults to `false`.
   - Added `Build_withTools_rendersToolGuidance` — asserts `"Tools available"` and `"documentation"`
     both appear when `toolsAvailable: true`.

Both new tests were added verbatim from the brief, appended after the last existing test
(`DefaultSystemPrompt_mentionsRepositoryContext`).

## TDD evidence

**RED** — command:
```
cd /home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7 && dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests"
```
Output (relevant line):
```
/home/.../tests/Naudit.Tests/PromtBuilderTests.cs(230,44): error CS1739: The best overload for
'Build' does not have a parameter named 'toolsAvailable' [.../Naudit.Tests.csproj]
```
Why this counts as RED: this is a build failure, exactly as the brief predicted ("BUILD FAIL —
`Build` has no `toolsAvailable` parameter"), confirming the new tests exercise code that does not
exist yet — not a false-positive from a typo or wrong test target.

**GREEN** — command:
```
cd /home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7 && dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests|FullyQualifiedName~ReviewServiceTests"
```
Output:
```
Passed!  - Failed:     0, Passed:    29, Skipped:     0, Total:    29, Duration: 88 ms - Naudit.Tests.dll (net10.0)
```

**Full suite** — command:
```
cd /home/bnau/workspace/Naudit/.claude/worktrees/feat-mcp-context7 && dotnet test Naudit.slnx
```
First run reported `Failed: 21, Passed: 326` — all 21 failures were `WebApplicationFactory`-based
integration tests throwing `System.IO.IOException: The configured user limit (128) on the number of
inotify instances has been reached...` (a sandbox/environment resource limit hit when many
`WebApplicationFactory` hosts spin up file watchers in parallel), not assertion failures. Verified
this was environmental, unrelated to the change:
- Re-ran one such failing test alone (`GitWorkspaceProviderTests.CheckoutAsync_throwsAndCleansUp_whenGitFails`)
  — passed in isolation.
- Re-ran the full suite with `-- xUnit.MaxParallelThreads=1` to avoid exhausting inotify instances:
  ```
  Passed!  - Failed:     0, Passed:   347, Skipped:     0, Total:   347, Duration: 9 s - Naudit.Tests.dll (net10.0)
  ```
  All 347 tests pass, confirming the change introduces no regressions.

## Files changed

- `src/Naudit.Core/Review/PromtBuilder.cs`
- `src/Naudit.Core/Review/ReviewService.cs`
- `tests/Naudit.Tests/PromtBuilderTests.cs`

## Self-review

- Diff matches the brief's Step 3/Step 4 code blocks verbatim (parameter list, helper method body
  and comment, `ReviewService` reordering and comment).
- `AppendToolGuidance` correctly no-ops (returns immediately) when `toolsAvailable` is false, so
  `Build_withoutTools_hasNoToolGuidance` (and all pre-existing tests, which never pass
  `toolsAvailable`) are unaffected — verified by the full green suite.
- Core rule intact: no new dependencies, no provider/platform-specific code introduced; the change
  is a pure string-building addition plus argument plumbing in `ReviewService`.
- Comments added are German, consistent with existing style in both files.
- Commit only staged the three files named in the brief; the untracked `.superpowers/` directory
  (task briefs/specs, pre-existing in the worktree) was left alone.

## Concerns

None. The brief's replace block matched the actual file content exactly, so no guessing was
required at any step.

## Commit

`496053c feat(review): Prompt-Guidance fürs Docs-Werkzeug (nur wenn Tools angeboten)`
