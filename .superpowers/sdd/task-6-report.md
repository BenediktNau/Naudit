# Task 6 Report: Real MCP connector + package + DI wiring (function-invocation)

**Branch:** `feat/mcp-context7`
**Commit:** `cb3ccc5` — `feat(mcp): echter McpClientToolConnector + DI-Verdrahtung (Function-Invocation-Loop)`
**Status:** DONE (Steps 1–5 + 7 complete; Step 6 manual E2E DEFERRED — no live services in this environment)

## What was implemented

### Step 1 — Package (`src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`)
Added `ModelContextProtocol.Core` version `1.4.1` to the `PackageReference` `ItemGroup`.
`dotnet restore Naudit.slnx` resolved it cleanly.

### Step 2 — Real connector (`src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs`, new)
`McpClientToolConnector(ILoggerFactory) : IMcpToolConnector`. Written verbatim from the brief with
**one SDK version-name reconciliation** (see below):
- Builds an `IClientTransport` per `McpServerConfig`: `stdio` ⇒ `StdioClientTransport`
  (`Name`/`Command`/`Arguments`), otherwise `http` ⇒ `HttpClientTransport` with
  `HttpClientTransportOptions { Name, Endpoint, TransportMode = HttpTransportMode.AutoDetect }` and an
  optional `Authorization: Bearer <ApiKey>` in `AdditionalHeaders`.
- `McpClient.CreateAsync(transport, null, loggerFactory, ct)` → `client.ListToolsAsync((RequestOptions?)null, ct)`
  → `[.. tools]` into `IReadOnlyList<AITool>` (`McpClientTool : AIFunction : AITool`).

**SDK reconciliation (as the brief instructed — member-name drift, not a logic rewrite):** the brief's
`client.ListToolsAsync((ListToolsRequestParams?)null, ct)` did not compile against `ModelContextProtocol.Core 1.4.1`
(CS9212: `ListToolsResult` is not spreadable). Verified against
`~/.nuget/packages/modelcontextprotocol.core/1.4.1/lib/net10.0/ModelContextProtocol.Core.xml`:
in 1.4.1 it is the **`ListToolsAsync(RequestOptions, CancellationToken)`** overload that returns
`IList<McpClientTool>` (auto-paginated — XML: *"A list of all available tools as McpClientTool instances"*),
while the `ListToolsRequestParams` overload returns the raw single-page `ListToolsResult`. Reconciliation:
disambiguating cast `(ListToolsRequestParams?)null` → `(RequestOptions?)null` and import
`ModelContextProtocol.Protocol` → `ModelContextProtocol` (namespace of `RequestOptions`). Logic (connect →
list all tools → return as `AITool`s) is unchanged. All other pinned members
(`McpClient.CreateAsync`, `HttpClientTransport(options, loggerFactory)`, `HttpClientTransportOptions.*`,
`StdioClientTransportOptions.*`, `HttpTransportMode.AutoDetect`) matched the XML exactly.

### Step 3 — DI wiring (`src/Naudit.Infrastructure/DependencyInjection.cs`)
1. **`IChatClient` wrap:** the registration now resolves the client from `AiClientFactory.Create(aiOptions, mcpOptions)`
   and, when `mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode`, wraps it with
   `.AsBuilder().UseFunctionInvocation(sp.GetService<ILoggerFactory>(), c => c.MaximumIterationsPerRequest = mcpOptions.MaxIterations).Build()`.
   ClaudeCode (CLI-native MCP) is deliberately **not** wrapped.
2. **Tool provider:** replaced the Task-1 unconditional `NullReviewToolProvider` registration with the conditional one —
   MCP-on + MEAI provider ⇒ register `IMcpToolConnector` = `McpClientToolConnector` and
   `IReviewToolProvider` = `McpReviewToolProvider`; otherwise ⇒ `NullReviewToolProvider`.

### Step 4 — Composition test (`tests/Naudit.Tests/McpDiCompositionTests.cs`, new)
Written verbatim from the brief. Uses the project's established minimal-config DI-test pattern (same as
`RedactionWiringTests`): `AddLogging()` + `AddNauditInfrastructure(config)` from an in-memory config, then a
full `BuildServiceProvider()` and `GetRequiredService<IReviewToolProvider>()`. No DB bootstrap was needed —
`IReviewToolProvider` (and its transitive deps `IMcpToolConnector`, `ILoggerFactory`) are plain singletons that
resolve without `AddNauditDatabase`; the `IChatClient` factory stays unevaluated because the test never resolves it.
The full-provider path was reliable, so the `ServiceDescriptor`-inspection fallback was not needed.

**How the test proves the 3 registration cases:**
- `McpDisabled_resolvesNullToolProvider` — MEAI provider, `Mcp:Enabled=false` ⇒ `NullReviewToolProvider` (MCP off → No-Op).
- `McpEnabled_meaiProvider_resolvesMcpToolProvider` — `OpenAICompatible` + `Mcp:Enabled=true` + one server ⇒ `McpReviewToolProvider` (the real MCP path).
- `McpEnabled_claudeCodeProvider_stillResolvesNullToolProvider` — `ClaudeCode` + `Mcp:Enabled=true` ⇒ `NullReviewToolProvider` (CLI-native MCP, so no `ChatOptions.Tools` provider), guarding the `Provider != ClaudeCode` condition.

### Step 5 — Full suite: PASS.

### Step 6 — Manual E2E (real Context7): **DEFERRED / PENDING for the controller.**
Requires a live Context7 MCP server + the real `claude` CLI + a running host — unavailable in this environment.
Not attempted; no E2E results fabricated. The connector's `Authorization: Bearer` header shape and the CLI
`--mcp-config` path still need live confirmation per the brief's Step 6.

## Verify evidence

```
$ dotnet restore Naudit.slnx
  Restored .../Naudit.Infrastructure.csproj ...   (ModelContextProtocol.Core 1.4.1 resolved)

$ dotnet build Naudit.slnx          # after the RequestOptions reconciliation
  Build succeeded.  0 Warning(s)  0 Error(s)

$ dotnet test Naudit.slnx --filter "FullyQualifiedName~McpDiCompositionTests"
  Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3

$ dotnet test Naudit.slnx                         # parallel
  Failed! - Failed: 22, Passed: 338, Total: 360   # all 22 = WebApplicationFactory inotify flake
    (PhysicalFilesWatcher.CreateFileChangeToken — the environmental flake the brief flagged)

$ dotnet test Naudit.slnx -- xUnit.MaxParallelThreads=1   # single-threaded confirm
  Passed!  - Failed: 0, Passed: 360, Skipped: 0, Total: 360
```

The 22 parallel failures were exclusively `WebApplicationFactory`-hosted tests failing inside
`PhysicalFilesWatcher.CreateFileChangeToken` (sandbox inotify limit) — none touch the MCP change. Single-threaded
re-run: 0 failures / 360 passed. Not a regression.

## Files changed
- `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj` (M) — `ModelContextProtocol.Core 1.4.1`.
- `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs` (new) — real `IMcpToolConnector`.
- `src/Naudit.Infrastructure/DependencyInjection.cs` (M) — function-invocation wrap + conditional provider registration.
- `tests/Naudit.Tests/McpDiCompositionTests.cs` (new) — 3-case DI composition test.

## Self-review
- **Core rule intact:** `grep -rn "ModelContextProtocol" src/Naudit.Core/` ⇒ no hits. The SDK lives only in `Naudit.Infrastructure`.
- **Condition consistency:** the `IChatClient` wrap and the tool-provider branch use the identical guard
  (`mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode`), so a wrapped function-invocation client
  always pairs with `McpReviewToolProvider`, and ClaudeCode never gets either — no half-wired state.
- **No dead config:** `using Microsoft.Extensions.AI;` (AsBuilder/UseFunctionInvocation) and
  `using Naudit.Infrastructure.Ai;` (AiProvider) were already present; build is warning-clean.
- **Commit hygiene:** only the 4 brief-named files staged; `.superpowers/` left untracked.
- **Brief fidelity:** connector, DI blocks, and test are verbatim except the one documented, XML-verified
  `RequestOptions` overload reconciliation the brief pre-authorized.

## Status
DONE. Manual E2E (Step 6) DEFERRED to the controller. `ModelContextProtocol.*` confined to Infrastructure.
