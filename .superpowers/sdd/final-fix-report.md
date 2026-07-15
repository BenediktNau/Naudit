# Final-review fixes — MCP-Tools in der Review-Runtime (Context7)

Branch: `feat/mcp-context7`. One fix commit applied on top of the merged feature.

## Fix 1 (security) — atomic 0600 temp file

File: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`

Replaced `File.WriteAllTextAsync` + subsequent `File.SetUnixFileMode` (a window where the
ApiKey-bearing temp file was world-readable at default umask, typically 0644) with atomic
creation via `FileStreamOptions.UnixCreateMode`:

```csharp
var fileOpts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
if (!OperatingSystem.IsWindows())
    fileOpts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;   // 0600 schon bei Erzeugung
await using (var fs = new FileStream(mcpConfigPath, fileOpts))
await using (var w = new StreamWriter(fs))
    await w.WriteAsync(BuildMcpConfigJson(mcp));
```

The file now never exists with world/group-readable permissions — 0600 is set by the OS at
`open()` time, before any byte is written. `--mcp-config <path>` arg and the `finally`-block
best-effort cleanup are unchanged.

## Fix 2 (security) — tighten validator anchor

Same file, `IsValidServerName`: changed `"^[A-Za-z0-9_-]+$"` to `@"\A[A-Za-z0-9_-]+\z"`. In .NET,
`$` also matches immediately before a trailing `\n`, so a server name like `"context7\n"` (or
`"context7\nBash"` when followed by another line) could previously slip past the check and taint
the `--allowedTools` allowlist string. `\A`/`\z` are true whole-string anchors with no such
exception.

## Fix 3 (docs) — data-egress note

File: `docs/mcp-tools.md`, "Opt-in and fail-open" section. Added a new bullet directly after the
existing "Opt-in" bullet:

> **New egress.** Enabling MCP opens an outbound channel this self-hosted bot didn't have before:
> once tools are on, the model sends library/API identifiers derived from the diff to the
> configured MCP server (e.g. `context7.com`) to resolve documentation. Opt in only once you're
> comfortable with that data leaving your deployment.

Matches the file's existing bullet style (bold lead-in + explanation), no restructuring.

## Fix 4 (maintainability) — single DI guard local

File: `src/Naudit.Infrastructure/DependencyInjection.cs`. Extracted
`mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode` into one local,
`mcpForMeaiProvider`, declared right after `mcpOptions` is bound and used at both call sites (the
`IChatClient` function-invocation wrap, and the `IReviewToolProvider`/`IMcpToolConnector`
registration `if`). Behavior identical — same boolean expression, now computed once instead of
twice.

## Tests added

`tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`: new
`GetResponseAsync_mcpEnabled_mcpConfigFile_hasUserOnlyPermissions` — reads
`File.GetUnixFileMode` on the `--mcp-config` path inside the stub responder (file still exists at
that point, same pattern as the existing `..._mcpConfigJson_containsServerUrl` test) and asserts
it equals `UserRead | UserWrite` (0600) on non-Windows.

## Verification

```
$ dotnet build Naudit.slnx
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"
Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17, Duration: 85 ms - Naudit.Tests.dll (net10.0)

$ dotnet test Naudit.slnx
Failed!  - Failed: 44, Passed: 317, Skipped: 0, Total: 361, Duration: 1 s - Naudit.Tests.dll (net10.0)
  (all 44 failures: System.IO.IOException — inotify instance limit (128) reached, from
   WebApplicationFactory<Program> host tests running in parallel — the documented sandbox flake)

$ dotnet test Naudit.slnx -- xUnit.MaxParallelThreads=1
Passed!  - Failed: 0, Passed: 361, Skipped: 0, Total: 361, Duration: 9 s - Naudit.Tests.dll (net10.0)
```

Single-threaded run confirms the parallel-run failures are the known environmental inotify flake
(sandbox `fs.inotify.max_user_instances` limit hit by concurrent
`WebApplicationFactory<Program>` hosts), not regressions from this change.

## Files changed

- `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs` — Fix 1 + Fix 2
- `src/Naudit.Infrastructure/DependencyInjection.cs` — Fix 4
- `docs/mcp-tools.md` — Fix 3
- `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs` — new 0600-perms test (nice-to-have, done)

## Concerns

None. All four fixes are narrow, behavior-preserving (except Fix 1's tightened window, which is
the intended security improvement, and Fix 2's tightened validation, ditto), and the CA1416
platform-compatibility warning that the new test initially triggered
(`File.GetUnixFileMode` unsupported on Windows) was resolved by gating the call behind
`!OperatingSystem.IsWindows()` at the call site itself, matching the pattern used elsewhere in
this file.
