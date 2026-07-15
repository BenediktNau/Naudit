# Task 3 Report: `McpOptions` config binding + SettingsCatalog entries

## What I implemented

Followed the brief verbatim, TDD (red → green → commit):

1. **`tests/Naudit.Tests/McpOptionsTests.cs`** (new) — two tests, copied verbatim from the brief:
   - `Binds_enabled_iterations_and_serverList` — binds `Naudit:Review:Mcp` from an in-memory
     `IConfiguration` (Enabled/MaxIterations/Servers:0:*) into `McpOptions` and asserts the values,
     including the nested `McpServerConfig` list entry.
   - `Catalog_hasEnabledAndMaxIterationsScalars` — asserts `SettingsCatalog.TryGet` finds
     `Naudit:Review:Mcp:Enabled` and `Naudit:Review:Mcp:MaxIterations`.

2. **`src/Naudit.Infrastructure/Mcp/McpOptions.cs`** (new) — `McpOptions` (`Enabled` bool,
   `MaxIterations` int default 4, `Servers` list) and `McpServerConfig` (`Name`, `Transport`
   default `"http"`, `Url`, `Command`, `Arguments`, `ApiKey`). Verbatim from the brief, German XML
   doc comments.

3. **`src/Naudit.Infrastructure/DependencyInjection.cs`** — added `using Naudit.Infrastructure.Mcp;`
   and, immediately after `var aiOptions = ...` and before
   `services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));`, bound
   `mcpOptions = configuration.GetSection("Naudit:Review:Mcp").Get<McpOptions>() ?? new McpOptions();`
   and registered it as a singleton. Comment and placement match the brief's Step 4 exactly (so
   Tasks 5–6, which change the `IChatClient` registration line right after it, can consume
   `mcpOptions`).

4. **`src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`** — added
   `new("Naudit:Review:Mcp:Enabled", false)` and `new("Naudit:Review:Mcp:MaxIterations", false)`
   to the `All` list, directly after the `Naudit:Review:Gate:MinConfidence` entry (before
   `Naudit:AccessGate:Mode`), as specified. Per-server `Servers:*` keys (including `ApiKey`) were
   **not** added — they stay env-only per the brief and the catalog's existing list-shaped-key
   precedent (`ProjectTokens`, `Ui:Admins`).

No changes to `Naudit.Core` — this is entirely an Infrastructure-project concern, per the project's
Core-isolation rule.

## TDD evidence

**RED** — `cd .../feat-mcp-context7 && dotnet test Naudit.slnx --filter "FullyQualifiedName~McpOptionsTests"`
(run before creating `McpOptions.cs`, before touching DI/catalog):

```
/home/.../tests/Naudit.Tests/McpOptionsTests.cs(2,29): error CS0234: The type or namespace name 'Mcp'
does not exist in the namespace 'Naudit.Infrastructure' (are you missing an assembly reference?)
```

Build failure as expected — `Naudit.Infrastructure.Mcp` didn't exist yet, so the test project
doesn't even compile. Matches the brief's "Expected: BUILD FAIL — `McpOptions` does not exist."

**GREEN** — same filtered command, after Steps 3–5:

```
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2, Duration: 12 ms - Naudit.Tests.dll (net10.0)
```

**Full suite** — `dotnet test Naudit.slnx` (default parallel):

```
Failed!  - Failed:    43, Passed:   306, Skipped:     0, Total:   349, Duration: 1 s - Naudit.Tests.dll (net10.0)
```

All 43 failures were `System.IO.IOException: The configured user limit (128) on the number of
inotify instances has been reached...` inside `WebApplicationFactory`-based host tests (e.g.
`DataEndpointTests.*`) — the exact sandbox flake called out in the task instructions, unrelated to
this change (none of the 43 failing tests touch MCP/settings/DI code paths).

Confirmed environmental by re-running single-threaded:
`dotnet test Naudit.slnx -- xUnit.MaxParallelThreads=1`

```
Passed!  - Failed:     0, Passed:   349, Skipped:     0, Total:   349, Duration: 9 s - Naudit.Tests.dll (net10.0)
```

All 349 tests pass, including the 2 new `McpOptionsTests`. No real regressions.

## Files changed

- `src/Naudit.Infrastructure/Mcp/McpOptions.cs` (new, 26 lines)
- `src/Naudit.Infrastructure/DependencyInjection.cs` (+7 lines: using + binding block)
- `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (+2 lines: catalog entries)
- `tests/Naudit.Tests/McpOptionsTests.cs` (new, 39 lines)

Commit: `544536f` — `feat(mcp): McpOptions binden (Naudit:Review:Mcp) + Settings-Katalog-Scalars`
(exact message from the brief's Step 7).

## Self-review

- Diffed the final `DependencyInjection.cs` change against the brief's Step 4 snippet — comment
  text, variable name, and placement (between `aiOptions` assignment and the `IChatClient`
  registration) match exactly.
- Verified `SettingsCatalog.cs` insertion point: the two new entries sit directly after
  `Naudit:Review:Gate:MinConfidence` and before `Naudit:AccessGate:Mode`, as instructed.
- Confirmed no `Servers:*`/`ApiKey` entries were added to the catalog (would have been wrong per
  the brief — those stay env-only, consistent with `ProjectTokens`/`Ui:Admins`).
- Checked `git status` before committing — only the four brief-specified files were staged; the
  untracked `.superpowers/` directory (pre-existing plan/spec docs from earlier tasks) was left
  alone, matching the exact `git add` file list in the brief.
- No `Naudit.Core` files touched — Core-isolation rule intact.
- `McpOptions`/`McpServerConfig` are plain POCOs with mutable public setters, consistent with the
  project's other `*Options` classes (`AiOptions`, `GitOptions`, `SastOptions`, etc.) that are bound
  the same way via `IConfiguration.Get<T>()`.

## Concerns

None. This was a small, mechanical, verbatim-from-brief task (no design decisions to make); Tasks
5–6 still need to actually consume `McpOptions` in the review pipeline (client wrap / tool-provider
registration) — out of scope here by design.
