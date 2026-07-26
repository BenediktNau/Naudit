# Task 1 Report — FindingCategory.Dast + prompt rendering

## Status: DONE

## What was implemented

1. **`src/Naudit.Core/Models/ScanFinding.cs`** — appended `Dast` as the last member of
   `FindingCategory` (`{ Sast, Sca, Secrets, Dast }`), preserving existing ordinals (0-2
   unchanged, Dast = 3). Updated the XML doc comment (German) to mention the new category.
2. **`src/Naudit.Core/Review/PromtBuilder.cs`** — added one line directly after the existing
   SAST `AppendCategory` call in `AppendFindings`:
   ```csharp
   AppendCategory(sb, "DAST (dynamic)", findings.Where(f => f.Category == FindingCategory.Dast));
   ```
   Reuses the existing `AppendCategory` helper (heading + `[SEVERITY][scope] tool · rule ·
   file:line → message` lines), so DAST findings render exactly like every other category —
   empty list renders nothing (section omitted), consistent with Secrets/SCA/SAST.
3. **`tests/Naudit.Tests/PromtBuilderTests.cs`** — added
   `Build_rendersDastFindings_underDynamicHeading`, placed after
   `Build_rendersSecretsFindings_beforeOtherCategories` (next to the other
   category-rendering tests). Asserts `"DAST (dynamic)"` heading and the finding message
   appear in the rendered user message.

## Adaptations from the brief

The brief's test sketch referenced `SystemPrompt`/`Request`/`Changes` "helper" fixtures that
do not exist in the real `PromtBuilderTests.cs` — every test in that file constructs its own
local `request`/`changes`/`findings` inline (e.g. `Build_rendersSecretsFindings_beforeOtherCategories`).
Adapted the test to that inline pattern, mirroring
`Build_rendersFindings_withScopeLabels`/`Build_rendersSecretsFindings_beforeOtherCategories`
exactly (same `ReviewRequest("1", 42, "T")` / single `CodeChange("a.cs", "@@ +1 @@")` shape),
and used `PromptBuilder.Build("SYS", request, changes, findings)[1].Text!` like the other
category tests instead of the brief's `PromptBuilder.Build(SystemPrompt, Request, Changes,
findings)` + `messages.Where(m => m.Role == ChatRole.User)...` form. Behaviourally identical;
assertions kept as specified (`"DAST (dynamic)"` and the XSS message substring).

Also: `--filter PromtBuilderTests` (brief's Step 5, matching the typo'd filename) does not
match anything — the class is `PromptBuilderTests` (no typo). Used `--filter
PromptBuilderTests` instead; noted here since the brief's exact filter string doesn't work
as written.

## TDD evidence

**RED** (`dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter
"FullyQualifiedName~Build_rendersDastFindings"`, before Steps 3-4):
```
tests/Naudit.Tests/PromtBuilderTests.cs(106,53): error CS0117: 'FindingCategory' does not
contain a definition for 'Dast' [.../Naudit.Tests.csproj]
```
Matches the brief's expected failure mode (CS0117).

**GREEN** (`dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`,
after Steps 3-4):
```
Passed!  - Failed: 0, Passed: 23, Skipped: 0, Total: 23, Duration: 131 ms
```
(22 pre-existing + 1 new).

**Build:** `dotnet build Naudit.slnx` → 0 Errors, 20 warnings (all pre-existing
`NU1903`/`System.Security.Cryptography.Xml` advisory noise, unrelated to this change).

**Full suite:** `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`:
- First run: 700 passed / 1 failed
  (`Naudit.Tests.GitWorkspaceProviderTests.CheckoutAsync_throwsAndCleansUp_whenGitFails`,
  `Assert.Empty()` on leftover temp dirs) — **unrelated** to this change (no touch to
  `GitWorkspaceProvider` or its tests).
- Verified pre-existing/flaky, not caused by this change: `git stash`'d this task's diff back
  to baseline HEAD `94ecb39` and ran that single test in isolation → passed (1/1). Restored
  the stash (`git stash pop`) and re-ran the **full** suite again with no isolation trick:
  **701 passed / 0 failed** (`Total: 701, Duration: 26s`), i.e. baseline 700 + 1 new test, as
  expected. The single earlier failure is consistent with test-parallelism/tmp-dir flakiness
  in `GitWorkspaceProviderTests`, not a regression from this task.

## Files changed

- `src/Naudit.Core/Models/ScanFinding.cs`
- `src/Naudit.Core/Review/PromtBuilder.cs`
- `tests/Naudit.Tests/PromtBuilderTests.cs`

## Self-review

- Enum member appended **last** — ordinals of `Sast`(0)/`Sca`(1)/`Secrets`(2) unchanged, so any
  persisted/serialized `FindingCategory` values (DB `ReviewFindingEntity`, if stored as int)
  stay stable. Confirmed no other file in the repo pattern-matches on `FindingCategory` via a
  `switch` that would need a new arm (grep confirms `AppendFindings` is the only consumer
  besides `ScanFinding` itself and test fixtures).
- Rendering follows the established `AppendCategory` pattern exactly (no new formatting code,
  no new branches) — empty-DAST-list stays byte-identical to today's prompt (verified
  implicitly: all pre-existing `Build_with*ByteIdentical` tests still pass).
- Core rule intact: no new dependency, `Naudit.Core` still only touches
  `Microsoft.Extensions.AI.Abstractions` types plus its own `Models`/`Review` namespace.
- Committed exactly the three intended files (`git status` before commit confirmed no
  unrelated pre-existing working-tree changes — `.superpowers/sdd/progress.md`,
  `.superpowers/sdd/task-1-brief.md`, and an untracked plan doc — were swept in).
- Commit message matches the brief's Step 7 exactly:
  `feat(dast): FindingCategory.Dast + Prompt-Sektion für dynamische Funde`.

## Concerns

- None blocking. The one pre-existing flaky test
  (`GitWorkspaceProviderTests.CheckoutAsync_throwsAndCleansUp_whenGitFails`) is worth a look
  by whoever owns DAST PR 1/sandbox work if it recurs, but it is out of scope for this task and
  not caused by this change (confirmed via isolated run against baseline HEAD and a clean
  second full-suite run at 701/701).
