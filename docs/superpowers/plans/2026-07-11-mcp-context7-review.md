# MCP tools in the review runtime (Context7) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Naudit an MCP client so the review LLM can call tools at review time, and land the first tool — Context7 (live library docs) — behind an opt-in, fail-open switch, without breaking the Core rule.

**Architecture:** A new Core abstraction `IReviewToolProvider` returns MEAI `AITool`s; `ReviewService` sets `chatOptions.Tools` from it. Two provider paths share one config: MEAI providers get tools via `ChatOptions.Tools` + a `UseFunctionInvocation` loop; the ClaudeCode CLI gets them via `--mcp-config` + a MCP-only `--allowedTools` allowlist. Everything is off by default and degrades to today's single-shot when a server is unreachable.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI 10.7.0 (`AsBuilder`/`UseFunctionInvocation`/`FunctionInvokingChatClient.MaximumIterationsPerRequest`), ModelContextProtocol.Core 1.4.1 (`McpClient.CreateAsync`, `HttpClientTransport`, `StdioClientTransport`, `McpClientTool : AIFunction`), xUnit.

## Global Constraints

- **Solution file is `Naudit.slnx`** (XML solution). `dotnet test Naudit.sln` fails with MSB1009 — always use `Naudit.slnx`.
- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. `AITool`/`AIFunction`/`ChatOptions`/`ChatMessage` live in that package and are allowed; the `ModelContextProtocol.*` SDK must **not** be referenced from Core.
- **Code comments are in German; docs/ are in English.**
- **MEAI/MCP member names are version-sensitive.** A missing-member build error at these spots means a package-version mismatch, not a logic bug.
- **Opt-in, fail-open:** with `Naudit:Review:Mcp:Enabled=false` the request/CLI args must be byte-identical to today. An unreachable server or tool error must never fail a review.
- **Commit after every task.** German commit messages, following the repo style. End commit bodies with the two trailer lines used by this repo (`Co-Authored-By:` and `Claude-Session:`).

---

## File Structure

**Create:**
- `src/Naudit.Core/Abstractions/IReviewToolProvider.cs` — Core seam + `NullReviewToolProvider` (no-op default).
- `src/Naudit.Infrastructure/Mcp/McpOptions.cs` — `McpOptions` + `McpServerConfig` (binds `Naudit:Review:Mcp`).
- `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs` — internal per-server connector seam.
- `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs` — orchestration: aggregate servers, fail-open, cache.
- `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs` — real MCP SDK wiring (manual-E2E only).
- `tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs` — returns a fixed tool list.
- `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs` — per-server fake (returns tools or throws).
- `tests/Naudit.Tests/McpReviewToolProviderTests.cs` — orchestration/fail-open/cache tests.
- `docs/mcp-tools.md` — feature doc.

**Modify:**
- `src/Naudit.Core/Review/ReviewService.cs` — inject `IReviewToolProvider`, set `chatOptions.Tools`, pass `hasTools` to `PromptBuilder`.
- `src/Naudit.Core/Review/PromtBuilder.cs` — `toolsAvailable` param + guidance block.
- `src/Naudit.Infrastructure/Ai/AiClientFactory.cs` — `Create(AiOptions, McpOptions?)`, pass MCP to ClaudeCode.
- `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs` — accept `McpOptions`, emit `--mcp-config`/`--allowedTools`/`--max-turns` when enabled.
- `src/Naudit.Infrastructure/DependencyInjection.cs` — bind `McpOptions`, register the right `IReviewToolProvider`, wrap the MEAI client with `UseFunctionInvocation`.
- `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` — two scalar entries.
- `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj` — add `ModelContextProtocol.Core` package.
- `tests/Naudit.Tests/Fakes/FakeChatClient.cs` — capture `LastOptions`.
- `tests/Naudit.Tests/ReviewServiceTests.cs` — helper + new tests.
- `tests/Naudit.Tests/PromtBuilderTests.cs` — guidance-block tests.
- `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs` — CLI-path tests.
- `docs/configuration.md` — MCP section link.

---

## Task 1: Core tool seam + ReviewService wiring (behavior unchanged)

**Files:**
- Create: `src/Naudit.Core/Abstractions/IReviewToolProvider.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeChatClient.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Produces: `IReviewToolProvider.GetToolsAsync(ReviewRequest, CancellationToken) → Task<IReadOnlyList<AITool>>`; `NullReviewToolProvider` (returns `[]`). `ReviewService` primary ctor gains a trailing `IReviewToolProvider toolProvider` parameter.

- [ ] **Step 1: Extend `FakeChatClient` to capture the options**

In `tests/Naudit.Tests/Fakes/FakeChatClient.cs`, add a property and set it in `GetResponseAsync`:

```csharp
public List<ChatMessage>? LastMessages { get; private set; }
public ChatOptions? LastOptions { get; private set; }

public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
{
    LastMessages = messages.ToList();
    LastOptions = options;
    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
}
```

- [ ] **Step 2: Add the fake tool provider**

Create `tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Liefert eine feste Tool-Liste — für ReviewService-Tests (kein echter MCP-Server nötig).
internal sealed class FakeReviewToolProvider(params AITool[] tools) : IReviewToolProvider
{
    public Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AITool>>(tools);
}
```

- [ ] **Step 3: Write the failing tests**

In `tests/Naudit.Tests/ReviewServiceTests.cs`, first update the `CreateService` helper to accept and default the new dependency (add the parameter and pass it last to the ctor):

```csharp
    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null,
        IPromptRedactor? redactor = null,
        IContextCollector? contextCollector = null,
        IReviewAuditSink? auditSink = null,
        IAiClientRouter? router = null,
        IReviewToolProvider? toolProvider = null)
        => new(router ?? new SingleClientRouter(chat), git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector(),
            auditSink ?? new FakeReviewAuditSink(),
            toolProvider ?? new NullReviewToolProvider());
```

Then add two tests:

```csharp
    [Fact]
    public async Task ReviewAsync_withoutToolProvider_leavesChatOptionsToolsNull()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Null(chat.LastOptions!.Tools);   // Feature aus ⇒ identischer Single-Shot
    }

    [Fact]
    public async Task ReviewAsync_withTools_populatesChatOptionsTools()
    {
        var tool = Microsoft.Extensions.AI.AIFunctionFactory.Create(() => "doc", "get_docs");
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            toolProvider: new FakeReviewToolProvider(tool));

        await service.ReviewAsync(Request);

        Assert.NotNull(chat.LastOptions!.Tools);
        Assert.Single(chat.LastOptions.Tools!);
        Assert.Equal("get_docs", chat.LastOptions.Tools![0].Name);
    }
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ReviewServiceTests"`
Expected: BUILD FAIL — `IReviewToolProvider` / `NullReviewToolProvider` do not exist yet.

- [ ] **Step 5: Create the Core seam**

Create `src/Naudit.Core/Abstractions/IReviewToolProvider.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Liefert die Tools, die dem LLM für diesen Review angeboten werden (leer = keine).
/// MEAI-Abstraktion AITool ist erlaubt; das Bauen aus MCP-Servern passiert in Infrastructure.</summary>
public interface IReviewToolProvider
{
    Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Default ohne MCP: keine Tools — identischer Single-Shot wie heute.</summary>
public sealed class NullReviewToolProvider : IReviewToolProvider
{
    public Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AITool>>([]);
}
```

- [ ] **Step 6: Wire it into `ReviewService`**

In `src/Naudit.Core/Review/ReviewService.cs`, add the ctor parameter (append after `auditSink`):

```csharp
    IContextCollector contextCollector,
    IReviewAuditSink auditSink,
    IReviewToolProvider toolProvider)
```

Then set the tools right before the chat call. Replace:

```csharp
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // Routing pro Review: Autor-Session oder globaler Client (Feature aus ⇒ immer global).
        var selection = await aiRouter.SelectAsync(request, ct);
        var response = await selection.Client.GetResponseAsync(messages, chatOptions, ct);
```

with:

```csharp
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure).
        var tools = await toolProvider.GetToolsAsync(request, ct);
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];

        // Routing pro Review: Autor-Session oder globaler Client (Feature aus ⇒ immer global).
        var selection = await aiRouter.SelectAsync(request, ct);
        var response = await selection.Client.GetResponseAsync(messages, chatOptions, ct);
```

- [ ] **Step 7: Register the default in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, immediately after the `IAiClientRouter` registration (`services.AddSingleton<IAiClientRouter>(...)`), add:

```csharp
        // MCP-Tools: Default No-Op (Task 6 registriert bei aktivem MCP + MEAI-Provider den echten Provider).
        services.AddSingleton<IReviewToolProvider>(new NullReviewToolProvider());
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ReviewServiceTests"`
Expected: PASS (all existing ReviewServiceTests + the two new ones).

- [ ] **Step 9: Commit**

```bash
git add src/Naudit.Core/Abstractions/IReviewToolProvider.cs src/Naudit.Core/Review/ReviewService.cs \
        src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/Fakes/FakeChatClient.cs \
        tests/Naudit.Tests/Fakes/FakeReviewToolProvider.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(review): IReviewToolProvider-Naht — ChatOptions.Tools aus Core (No-Op-Default)"
```

---

## Task 2: PromptBuilder tool-guidance block

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `PromptBuilder.Build(string, ReviewRequest, IReadOnlyList<CodeChange>, IReadOnlyList<ScanFinding>?, ReviewContext?, bool toolsAvailable = false)` — new trailing optional param.

- [ ] **Step 1: Write the failing tests**

In `tests/Naudit.Tests/PromtBuilderTests.cs`, add:

```csharp
    [Fact]
    public void Build_withoutTools_hasNoToolGuidance()
    {
        var msgs = PromptBuilder.Build("SYS", new ReviewRequest("1", 1, "T"),
            [new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);

        Assert.DoesNotContain("Tools available", msgs[1].Text);
    }

    [Fact]
    public void Build_withTools_rendersToolGuidance()
    {
        var msgs = PromptBuilder.Build("SYS", new ReviewRequest("1", 1, "T"),
            [new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")],
            findings: null, context: null, toolsAvailable: true);

        Assert.Contains("Tools available", msgs[1].Text);
        Assert.Contains("documentation", msgs[1].Text);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests"`
Expected: BUILD FAIL — `Build` has no `toolsAvailable` parameter.

- [ ] **Step 3: Add the parameter and guidance block**

In `src/Naudit.Core/Review/PromtBuilder.cs`, change the `Build` signature:

```csharp
    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null,
        bool toolsAvailable = false)
```

Then, inside `Build`, after `AppendFindings(sb, findings ?? []);` and before the `return`, add:

```csharp
        AppendToolGuidance(sb, toolsAvailable);
```

And add the helper method (below `AppendFindings`):

```csharp
    // Nur wenn dem Review Tools angeboten werden: knapper Hinweis, WANN das Docs-Werkzeug sinnvoll ist.
    // Ohne Hinweis ruft das Modell das Tool nie oder ständig — beides verbrennt Budget.
    private static void AppendToolGuidance(StringBuilder sb, bool toolsAvailable)
    {
        if (!toolsAvailable)
            return;
        sb.AppendLine();
        sb.AppendLine("# Tools available");
        sb.AppendLine("You can call a tool to fetch current documentation for a library (Context7). " +
            "Use it when the diff uses an API you are unsure about, rather than guessing against possibly-outdated knowledge. " +
            "Do not use it for well-known stdlib or trivial code. After any tool use, still respond with the required review JSON.");
    }
```

- [ ] **Step 4: Pass the flag from `ReviewService`**

In `src/Naudit.Core/Review/ReviewService.cs`, the tool fetch now happens before `PromptBuilder.Build`. Move the tool fetch above the `messages` line and pass `hasTools`. Replace this block:

```csharp
        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure).
        var tools = await toolProvider.GetToolsAsync(request, ct);
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];
```

with:

```csharp
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure) + Hinweis im Prompt.
        var tools = await toolProvider.GetToolsAsync(request, ct);
        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext,
            toolsAvailable: tools.Count > 0);

        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~PromtBuilderTests|FullyQualifiedName~ReviewServiceTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(review): Prompt-Guidance fürs Docs-Werkzeug (nur wenn Tools angeboten)"
```

---

## Task 3: `McpOptions` config binding + SettingsCatalog entries

**Files:**
- Create: `src/Naudit.Infrastructure/Mcp/McpOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `tests/Naudit.Tests/` — add `McpOptionsTests.cs`

**Interfaces:**
- Produces: `McpOptions { bool Enabled; int MaxIterations; List<McpServerConfig> Servers }`; `McpServerConfig { string Name; string Transport; string? Url; string? Command; List<string>? Arguments; string? ApiKey }`. Registered as a singleton `McpOptions` in DI.

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/McpOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Mcp;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

public class McpOptionsTests
{
    [Fact]
    public void Binds_enabled_iterations_and_serverList()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:MaxIterations"] = "6",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Transport"] = "http",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
            ["Naudit:Review:Mcp:Servers:0:ApiKey"] = "sk-123",
        }).Build();

        var opts = config.GetSection("Naudit:Review:Mcp").Get<McpOptions>()!;

        Assert.True(opts.Enabled);
        Assert.Equal(6, opts.MaxIterations);
        var server = Assert.Single(opts.Servers);
        Assert.Equal("context7", server.Name);
        Assert.Equal("https://mcp.context7.com/mcp", server.Url);
        Assert.Equal("sk-123", server.ApiKey);
    }

    [Fact]
    public void Catalog_hasEnabledAndMaxIterationsScalars()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:Enabled", out _));
        Assert.True(SettingsCatalog.TryGet("Naudit:Review:Mcp:MaxIterations", out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpOptionsTests"`
Expected: BUILD FAIL — `McpOptions` does not exist.

- [ ] **Step 3: Create `McpOptions`**

Create `src/Naudit.Infrastructure/Mcp/McpOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Mcp;

/// <summary>Naudit:Review:Mcp — MCP-Tools in der Review-Runtime. Enabled=false ⇒ heutiger Single-Shot.</summary>
public sealed class McpOptions
{
    /// <summary>Master-Schalter. Aus ⇒ keine Tools, kein Function-Invocation-Loop.</summary>
    public bool Enabled { get; set; }

    /// <summary>Obergrenze der Tool-Runden pro Review (Token-/Latenz-Schutz). Beide Provider-Pfade.</summary>
    public int MaxIterations { get; set; } = 4;

    /// <summary>Konfigurierte MCP-Server (Liste ⇒ env-/appsettings-geformt, wie ProjectTokens).</summary>
    public List<McpServerConfig> Servers { get; set; } = new();
}

/// <summary>Ein MCP-Server. Transport "http" (Url) oder "stdio" (Command/Arguments).
/// ApiKey (Secret) wird bei http als Authorization-Bearer-Header gesetzt.</summary>
public sealed class McpServerConfig
{
    public string Name { get; set; } = "";
    public string Transport { get; set; } = "http";
    public string? Url { get; set; }
    public string? Command { get; set; }
    public List<string>? Arguments { get; set; }
    public string? ApiKey { get; set; }
}
```

- [ ] **Step 4: Bind it in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, bind `mcpOptions` **immediately after** `var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();` and **before** the `services.AddSingleton<IChatClient>(...)` line — so Tasks 5–6 (which change that registration) can use it:

```csharp
        // MCP-Runtime-Config (Naudit:Review:Mcp). Vor der IChatClient-Registrierung binden, damit der
        // Client-Wrap + der ClaudeCode-CLI-Pfad sie teilen. Singleton für die Review-Pipeline.
        var mcpOptions = configuration.GetSection("Naudit:Review:Mcp").Get<McpOptions>() ?? new McpOptions();
        services.AddSingleton(mcpOptions);
```

Add the using at the top of the file if not present:

```csharp
using Naudit.Infrastructure.Mcp;
```

- [ ] **Step 5: Add the two scalar catalog entries**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`, add to the `All` list after the `Naudit:Review:Gate:MinConfidence` entry:

```csharp
        new("Naudit:Review:Mcp:Enabled", false),
        new("Naudit:Review:Mcp:MaxIterations", false),
```

(The per-server `Servers:*` keys — including `ApiKey` — stay env-only, following the `ProjectTokens`/`Ui:Admins` list-shaped precedent noted in the catalog's own comment.)

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpOptionsTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Mcp/McpOptions.cs src/Naudit.Infrastructure/DependencyInjection.cs \
        src/Naudit.Infrastructure/Settings/SettingsCatalog.cs tests/Naudit.Tests/McpOptionsTests.cs
git commit -m "feat(mcp): McpOptions binden (Naudit:Review:Mcp) + Settings-Katalog-Scalars"
```

---

## Task 4: `McpReviewToolProvider` orchestration (fail-open + cache)

**Files:**
- Create: `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs`
- Create: `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs`
- Create: `tests/Naudit.Tests/McpReviewToolProviderTests.cs`

**Interfaces:**
- Consumes: `McpOptions`, `McpServerConfig` (Task 3); `IReviewToolProvider` (Task 1).
- Produces: `IMcpToolConnector.ConnectAndListAsync(McpServerConfig, CancellationToken) → Task<IReadOnlyList<AITool>>`; `McpReviewToolProvider(McpOptions, IMcpToolConnector, ILogger<McpReviewToolProvider>)`.

- [ ] **Step 1: Add the connector seam and the fake**

Create `src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Verbindet EINEN MCP-Server und listet dessen Tools als MEAI-AITool. Seam, damit
/// McpReviewToolProvider ohne echten Server getestet wird (echte Impl: McpClientToolConnector).</summary>
public interface IMcpToolConnector
{
    Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default);
}
```

Create `tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Mcp;

namespace Naudit.Tests.Fakes;

// Pro Server-Name: entweder eine Tool-Liste oder eine Exception (für Fail-open-Tests). Zählt Aufrufe.
internal sealed class FakeMcpToolConnector : IMcpToolConnector
{
    private readonly Dictionary<string, Func<IReadOnlyList<AITool>>> _byServer = new();
    public int CallCount { get; private set; }

    public FakeMcpToolConnector Returns(string server, params AITool[] tools)
    {
        _byServer[server] = () => tools;
        return this;
    }

    public FakeMcpToolConnector Throws(string server)
    {
        _byServer[server] = () => throw new InvalidOperationException($"boom:{server}");
        return this;
    }

    public Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default)
    {
        CallCount++;
        if (_byServer.TryGetValue(server.Name, out var f))
            return Task.FromResult(f());
        return Task.FromResult<IReadOnlyList<AITool>>([]);
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `tests/Naudit.Tests/McpReviewToolProviderTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Mcp;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class McpReviewToolProviderTests
{
    private static readonly ReviewRequest Request = new("1", 1, "T");

    private static McpServerConfig Server(string name) => new() { Name = name, Transport = "http", Url = "http://x" };
    private static AITool Tool(string name) => AIFunctionFactory.Create(() => "r", name);

    private static McpReviewToolProvider Provider(McpOptions opts, IMcpToolConnector connector)
        => new(opts, connector, NullLogger<McpReviewToolProvider>.Instance);

    [Fact]
    public async Task Disabled_returnsEmpty_andNeverCallsConnector()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t"));
        var opts = new McpOptions { Enabled = false, Servers = { Server("a") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Empty(tools);
        Assert.Equal(0, connector.CallCount);
    }

    [Fact]
    public async Task Aggregates_toolsFromAllServers()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1")).Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        Assert.Equal(2, tools.Count);
    }

    [Fact]
    public async Task FailingServer_isSkipped_othersStillReturn()
    {
        var connector = new FakeMcpToolConnector().Throws("a").Returns("b", Tool("t2"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a"), Server("b") } };

        var tools = await Provider(opts, connector).GetToolsAsync(Request);

        var tool = Assert.Single(tools);
        Assert.Equal("t2", tool.Name);
    }

    [Fact]
    public async Task Caches_nonEmptyResult_acrossCalls()
    {
        var connector = new FakeMcpToolConnector().Returns("a", Tool("t1"));
        var opts = new McpOptions { Enabled = true, Servers = { Server("a") } };
        var provider = Provider(opts, connector);

        await provider.GetToolsAsync(Request);
        await provider.GetToolsAsync(Request);

        Assert.Equal(1, connector.CallCount);   // zweiter Review nutzt den Cache
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"`
Expected: BUILD FAIL — `McpReviewToolProvider` does not exist.

- [ ] **Step 4: Implement the provider**

Create `src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Baut die MEAI-Tools aus den konfigurierten MCP-Servern. Fail-open: ein nicht
/// erreichbarer Server ⇒ dieser Server fällt weg, der Review läuft (tool-los) weiter — wie ein
/// fehlgeschlagener SAST-Checkout → diff-only. Erfolgreiche Tool-Liste wird für die Prozesslaufzeit
/// gecacht (Server-Host fix, Katalog stabil); ein leeres Ergebnis wird NICHT gecacht (nächster
/// Review versucht erneut, damit ein zwischenzeitlich erreichbarer Server aufgenommen wird).</summary>
public sealed class McpReviewToolProvider(
    McpOptions options, IMcpToolConnector connector, ILogger<McpReviewToolProvider> logger) : IReviewToolProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<AITool>? _cached;

    public async Task<IReadOnlyList<AITool>> GetToolsAsync(ReviewRequest request, CancellationToken ct = default)
    {
        if (!options.Enabled || options.Servers.Count == 0)
            return [];

        if (_cached is { Count: > 0 })
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is { Count: > 0 })
                return _cached;

            var tools = new List<AITool>();
            foreach (var server in options.Servers)
            {
                try
                {
                    tools.AddRange(await connector.ConnectAndListAsync(server, ct));
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "MCP-Server {Server} nicht erreichbar — Review läuft ohne dessen Tools.", server.Name);
                }
            }

            if (tools.Count > 0)
                _cached = tools;   // nur Erfolg cachen
            return tools;
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~McpReviewToolProviderTests"`
Expected: PASS (all four).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Mcp/IMcpToolConnector.cs src/Naudit.Infrastructure/Mcp/McpReviewToolProvider.cs \
        tests/Naudit.Tests/Fakes/FakeMcpToolConnector.cs tests/Naudit.Tests/McpReviewToolProviderTests.cs
git commit -m "feat(mcp): McpReviewToolProvider — Server aggregieren, fail-open, cachen"
```

---

## Task 5: ClaudeCode CLI path (`--mcp-config` / `--allowedTools` / `--max-turns`)

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`
- Modify: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`

**Interfaces:**
- Consumes: `McpOptions` (Task 3).
- Produces: `AiClientFactory.Create(AiOptions, McpOptions?)`; `ClaudeCodeChatClient(AiOptions, IProcessRunner, McpOptions?)`.

- [ ] **Step 1: Write the failing tests**

In `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`, add a small helper and three tests. The `Client` helper currently builds a client without `McpOptions`; add an overload path — the client's third ctor arg is the new `McpOptions?` (null = today's behaviour):

```csharp
    [Fact]
    public async Task GetResponseAsync_mcpDisabled_keepsTodaysArgs()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub,
            new Naudit.Infrastructure.Mcp.McpOptions { Enabled = false });

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("1", args[args.IndexOf("--max-turns") + 1]);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);   // Tools aus
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--allowedTools", args);
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_addsMcpConfig_allowlist_andRaisesMaxTurns()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            MaxIterations = 5,
            Servers =
            {
                new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp", ApiKey = "sk-1" },
            },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        Assert.Equal("5", args[args.IndexOf("--max-turns") + 1]);         // Loop erlaubt
        Assert.Contains("--mcp-config", args);
        Assert.Contains("--allowedTools", args);
        // Allowlist enthält NUR das MCP-Tool des Servers — kein Bash/Edit/Read.
        var allow = args[args.IndexOf("--allowedTools") + 1];
        Assert.Contains("mcp__context7", allow);
        Assert.DoesNotContain("Bash", allow);
        Assert.DoesNotContain("Edit", allow);
        Assert.DoesNotContain("Read", allow);
        Assert.DoesNotContain("--tools", args);   // ersetzt durch die Allowlist
    }

    [Fact]
    public async Task GetResponseAsync_mcpEnabled_mcpConfigJson_containsServerUrl()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var mcp = new Naudit.Infrastructure.Mcp.McpOptions
        {
            Enabled = true,
            Servers = { new() { Name = "context7", Transport = "http", Url = "https://mcp.context7.com/mcp" } },
        };
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet" }, stub, mcp);

        await client.GetResponseAsync(Messages());

        var args = stub.LastSpec!.Arguments.ToList();
        var json = args[args.IndexOf("--mcp-config") + 1];
        Assert.Contains("context7", json);
        Assert.Contains("https://mcp.context7.com/mcp", json);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: BUILD FAIL — the 3-arg `ClaudeCodeChatClient` ctor does not exist.

- [ ] **Step 3: Add the MCP branch to `ClaudeCodeChatClient`**

In `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`, add the using:

```csharp
using Naudit.Infrastructure.Mcp;
```

Change the primary constructor to accept optional `McpOptions`:

```csharp
public sealed class ClaudeCodeChatClient(AiOptions aiOptions, IProcessRunner runner, McpOptions? mcp = null) : IChatClient
```

Replace the args-building block. Current:

```csharp
        var args = new List<string>
        {
            "-p", "--output-format", "json", "--max-turns", "1", "--tools", "", "--model", model,
        };
```

with:

```csharp
        // MCP an ⇒ Loop erlauben (--max-turns N), Server registrieren (--mcp-config) und AUSSCHLIESSLICH
        // die MCP-Tools freigeben (--allowedTools mcp__<server>) — die eingebauten Datei-/Shell-Tools
        // (Bash/Edit/Read/Write) bleiben aus. MCP aus ⇒ exakt die heutigen Args (--tools "", --max-turns 1).
        var mcpEnabled = mcp is { Enabled: true, Servers.Count: > 0 };
        var args = new List<string> { "-p", "--output-format", "json", "--model", model };
        if (mcpEnabled)
        {
            args.Add("--max-turns");
            args.Add(mcp!.MaxIterations.ToString(System.Globalization.CultureInfo.InvariantCulture));
            args.Add("--mcp-config");
            args.Add(BuildMcpConfigJson(mcp));
            args.Add("--allowedTools");
            args.Add(string.Join(" ", mcp.Servers.Select(s => $"mcp__{s.Name}")));   // nur die MCP-Server
        }
        else
        {
            args.Add("--max-turns");
            args.Add("1");
            args.Add("--tools");
            args.Add("");   // Tools aus (heutiges Verhalten)
        }
```

Add the helper method (place it near `StripJsonFences`), building the Claude Code `--mcp-config` shape (`{"mcpServers":{ "<name>": { ... } }}`):

```csharp
    // Baut das Claude-Code --mcp-config-JSON aus der geteilten McpOptions-Config.
    // http ⇒ { "type":"http", "url":..., "headers": { "Authorization":"Bearer <key>" } };
    // stdio ⇒ { "command":..., "args":[...] }.
    private static string BuildMcpConfigJson(McpOptions mcp)
    {
        var servers = new Dictionary<string, object>();
        foreach (var s in mcp.Servers)
        {
            if (string.Equals(s.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
            {
                servers[s.Name] = new Dictionary<string, object?>
                {
                    ["command"] = s.Command,
                    ["args"] = s.Arguments ?? new List<string>(),
                };
            }
            else
            {
                var entry = new Dictionary<string, object?> { ["type"] = "http", ["url"] = s.Url };
                if (!string.IsNullOrWhiteSpace(s.ApiKey))
                    entry["headers"] = new Dictionary<string, string> { ["Authorization"] = $"Bearer {s.ApiKey}" };
                servers[s.Name] = entry;
            }
        }
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["mcpServers"] = servers });
    }
```

- [ ] **Step 4: Thread `McpOptions` through `AiClientFactory`**

In `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`, add the using:

```csharp
using Naudit.Infrastructure.Mcp;
```

Change the signature and the ClaudeCode case:

```csharp
    public static IChatClient Create(AiOptions options, McpOptions? mcp = null)
```

```csharp
            case AiProvider.ClaudeCode:
                // Kein RequireApiKey: die CLI authentifiziert über die Umgebung (Abo statt Key).
                // MCP-Config wird an den CLI-Client durchgereicht (CLI-natives MCP, nicht ChatOptions.Tools).
                return new ClaudeCodeChatClient(options, new SystemProcessRunner(), mcp);
```

- [ ] **Step 5: Pass `mcpOptions` at the DI call site**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, `mcpOptions` is already bound just above this registration (Task 3, Step 4). Change the `IChatClient` registration from:

```csharp
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));
```

to:

```csharp
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions, mcpOptions));
```

(Task 6 replaces this lambda again to add the function-invocation wrapper.)

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests"`
Expected: PASS (existing arg tests still green because MCP defaults to off; three new tests pass).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs src/Naudit.Infrastructure/Ai/AiClientFactory.cs \
        src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/ClaudeCodeChatClientTests.cs
git commit -m "feat(mcp): ClaudeCode-CLI-Pfad — --mcp-config + MCP-only-Allowlist + --max-turns"
```

---

## Task 6: Real MCP connector + package + DI wiring (function-invocation)

**Files:**
- Modify: `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`
- Create: `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Create: `tests/Naudit.Tests/McpDiCompositionTests.cs`

**Interfaces:**
- Consumes: `IMcpToolConnector`, `McpReviewToolProvider` (Task 4); `McpOptions` (Task 3); `IReviewToolProvider`/`NullReviewToolProvider` (Task 1).
- Produces: `McpClientToolConnector : IMcpToolConnector`; conditional DI registration.

- [ ] **Step 1: Add the `ModelContextProtocol.Core` package**

In `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`, add inside the `<ItemGroup>` with the other `PackageReference`s:

```xml
    <PackageReference Include="ModelContextProtocol.Core" Version="1.4.1" />
```

Run: `dotnet restore Naudit.slnx`
Expected: restore succeeds, `ModelContextProtocol.Core 1.4.1` resolved.

- [ ] **Step 2: Implement the real connector**

Create `src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Naudit.Infrastructure.Mcp;

/// <summary>Echte MCP-Anbindung über das ModelContextProtocol-SDK. NUR manuell E2E getestet
/// (wie der reale Git-/LLM-Pfad) — die Orchestrierung/Fail-open deckt McpReviewToolProviderTests ab.
/// Der McpClient bleibt über die zurückgegebenen Tool-Referenzen am Leben (Tool-Aufruf ruft über ihn),
/// die Verbindung lebt bewusst für die Prozesslaufzeit (Singleton-Provider cached die Tools).</summary>
public sealed class McpClientToolConnector(ILoggerFactory loggerFactory) : IMcpToolConnector
{
    public async Task<IReadOnlyList<AITool>> ConnectAndListAsync(McpServerConfig server, CancellationToken ct = default)
    {
        var transport = BuildTransport(server);
        var client = await McpClient.CreateAsync(transport, null, loggerFactory, ct);
        // Overload-Disambiguierung: getypter null-Params-Wert (SDK-Version-sensibel).
        var tools = await client.ListToolsAsync((ListToolsRequestParams?)null, ct);
        return [.. tools];   // McpClientTool : AIFunction : AITool
    }

    // http ⇒ HttpClientTransport (Streamable-HTTP/SSE auto), ApiKey als Authorization-Bearer-Header.
    // stdio ⇒ StdioClientTransport (lokaler Prozess).
    private IClientTransport BuildTransport(McpServerConfig server)
    {
        if (string.Equals(server.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command ?? throw new InvalidOperationException($"MCP-Server {server.Name}: Command fehlt (stdio)."),
                Arguments = server.Arguments,
            });
        }

        var options = new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url ?? throw new InvalidOperationException($"MCP-Server {server.Name}: Url fehlt (http).")),
            TransportMode = HttpTransportMode.AutoDetect,
        };
        if (!string.IsNullOrWhiteSpace(server.ApiKey))
            options.AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {server.ApiKey}" };
        return new HttpClientTransport(options, loggerFactory);
    }
}
```

> **Note for the implementer:** the `McpClient.CreateAsync` / `ListToolsAsync` overloads and `HttpClientTransportOptions` property names are pinned to `ModelContextProtocol.Core 1.4.1` (verified against its XML docs). If a build error appears here after a package bump, it is a version-name drift, not a logic bug — reconcile against the new package's `ModelContextProtocol.Core.xml`. The exact Context7 auth header may differ from `Authorization: Bearer`; confirm during manual E2E (Step 6) and adjust `AdditionalHeaders`.

- [ ] **Step 3: Wire the conditional DI registration**

In `src/Naudit.Infrastructure/DependencyInjection.cs`:

First, wrap the MEAI client with the function-invocation loop. Change the `IChatClient` registration (from Task 5) to:

```csharp
        // Global-Client. Bei aktivem MCP + MEAI-Provider mit Function-Invocation-Loop umhüllen
        // (Cap = MaxIterations). ClaudeCode ist ein eigener IChatClient (CLI-natives MCP) und wird NICHT umhüllt.
        services.AddSingleton<IChatClient>(sp =>
        {
            var client = AiClientFactory.Create(aiOptions, mcpOptions);
            if (mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode)
                client = client.AsBuilder()
                    .UseFunctionInvocation(sp.GetService<ILoggerFactory>(),
                        c => c.MaximumIterationsPerRequest = mcpOptions.MaxIterations)
                    .Build();
            return client;
        });
```

Then, replace the default `NullReviewToolProvider` registration (added in Task 1) with the conditional one:

```csharp
        // MCP-Tools: MEAI-Provider + MCP an ⇒ echte MCP-Tools (Function-Invocation nutzt ChatOptions.Tools);
        // sonst No-Op (MCP aus, oder ClaudeCode ⇒ CLI-natives MCP über --mcp-config).
        if (mcpOptions.Enabled && aiOptions.Provider != AiProvider.ClaudeCode)
        {
            services.AddSingleton<IMcpToolConnector>(sp => new McpClientToolConnector(sp.GetRequiredService<ILoggerFactory>()));
            services.AddSingleton<IReviewToolProvider>(sp => new McpReviewToolProvider(
                mcpOptions,
                sp.GetRequiredService<IMcpToolConnector>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<McpReviewToolProvider>()));
        }
        else
        {
            services.AddSingleton<IReviewToolProvider>(new NullReviewToolProvider());
        }
```

Ensure `using Microsoft.Extensions.AI;` is present (for `AsBuilder`/`UseFunctionInvocation`) — it is used elsewhere; add if the build complains.

- [ ] **Step 4: Write the DI composition test**

Create `tests/Naudit.Tests/McpDiCompositionTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Mcp;
using Xunit;

namespace Naudit.Tests;

public class McpDiCompositionTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void McpDisabled_resolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "false",
        });

        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_meaiProvider_resolvesMcpToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "OpenAICompatible",
            ["Naudit:Ai:ApiKey"] = "k",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        Assert.IsType<McpReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }

    [Fact]
    public void McpEnabled_claudeCodeProvider_stillResolvesNullToolProvider()
    {
        using var sp = Build(new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "ClaudeCode",
            ["Naudit:Review:Mcp:Enabled"] = "true",
            ["Naudit:Review:Mcp:Servers:0:Name"] = "context7",
            ["Naudit:Review:Mcp:Servers:0:Url"] = "https://mcp.context7.com/mcp",
        });

        // ClaudeCode nutzt CLI-natives MCP ⇒ kein ChatOptions.Tools-Provider.
        Assert.IsType<NullReviewToolProvider>(sp.GetRequiredService<IReviewToolProvider>());
    }
}
```

> **Note:** `AddNauditInfrastructure` reads more than the AI section (git platform, DB, etc.). If building the full provider requires additional config keys or a DB, mirror the setup used by the existing infrastructure/DI tests in this project (check `TestAppFactory.cs` / any existing `*DiTests`/`DependencyInjection` test for the minimal key set and DB bootstrap) and reuse that pattern here. If a full `BuildServiceProvider()` is impractical in a unit test, assert the registration instead by inspecting the `IServiceCollection` (find the `ServiceDescriptor` for `IReviewToolProvider` and assert its `ImplementationType`/instance) without building the provider.

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS (whole suite, including the new DI composition tests).

- [ ] **Step 6: Manual E2E (real Context7)**

This is the real-path verification (CI never hits a live MCP server, mirroring the project's manual end-to-end convention).

1. Set config (user-secrets on the Web project), e.g. for an OpenAI-compatible provider:
   ```bash
   dotnet user-secrets set "Naudit:Review:Mcp:Enabled" "true" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Name" "context7" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Transport" "http" --project src/Naudit.Web
   dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:Url" "https://mcp.context7.com/mcp" --project src/Naudit.Web
   # optional: dotnet user-secrets set "Naudit:Review:Mcp:Servers:0:ApiKey" "<context7-key>" --project src/Naudit.Web
   ```
2. `dotnet run --project src/Naudit.Web`, trigger a review (webhook or `POST /review`) on a diff that uses a well-known library API.
3. Confirm in the logs that the tool was offered and (for a relevant diff) called, and that a valid review JSON still came back. If the Context7 server rejects the auth header, adjust `AdditionalHeaders` / the `--mcp-config` `headers` shape per Context7's docs.
4. Repeat with `Naudit:Ai:Provider=ClaudeCode` to exercise the CLI `--mcp-config` path (requires the `claude` CLI logged in).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Infrastructure/Mcp/McpClientToolConnector.cs \
        src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/McpDiCompositionTests.cs
git commit -m "feat(mcp): echter McpClientToolConnector + DI-Verdrahtung (Function-Invocation-Loop)"
```

---

## Task 7: Documentation

**Files:**
- Create: `docs/mcp-tools.md`
- Modify: `docs/configuration.md`

- [ ] **Step 1: Write `docs/mcp-tools.md`**

Create `docs/mcp-tools.md` (English, matching the other docs) covering:

- What it does: the review LLM can call MCP tools at review time; the first tool is Context7 (live library docs).
- The two provider paths (MEAI `ChatOptions.Tools` + function-invocation loop; ClaudeCode CLI `--mcp-config` + MCP-only `--allowedTools`), and that the built-in CLI file/shell tools stay off.
- Opt-in and fail-open behaviour (`Naudit:Review:Mcp:Enabled=false` ⇒ today's single-shot; unreachable server ⇒ tool-less review).
- The full config block (from the spec), including that the per-server `ApiKey` is env-only (list-shaped), while `Enabled`/`MaxIterations` are DB-manageable via Settings.
- The iteration cap (`MaxIterations`) and why it exists (token/latency).
- A pointer that Playwright/DAST are separate future slices (B/C), not part of this.

- [ ] **Step 2: Link it from `docs/configuration.md`**

Add a short "MCP tools (review runtime)" subsection to `docs/configuration.md` that summarizes the `Naudit:Review:Mcp:*` keys and links to `docs/mcp-tools.md`.

- [ ] **Step 3: Commit**

```bash
git add docs/mcp-tools.md docs/configuration.md
git commit -m "docs(mcp): MCP-Tools in der Review-Runtime + Konfiguration"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** Core-thin seam (Task 1), prompt guidance (Task 2), config + catalog (Task 3), fail-open/cache orchestration (Task 4), ClaudeCode CLI path with MCP-only allowlist (Task 5), real connector + function-invocation + iteration cap + conditional wiring (Task 6), docs (Task 7). Every spec section maps to a task.
- **Core rule:** only `IReviewToolProvider` (returning MEAI `AITool`) and `NullReviewToolProvider` live in Core; all `ModelContextProtocol.*` usage is in `Naudit.Infrastructure/Mcp/`. Verified: `Naudit.Core.csproj` references only `Microsoft.Extensions.AI.Abstractions`.
- **No behaviour change when off:** Task 1 keeps `ChatOptions.Tools` null with the null provider; Task 5 keeps the ClaudeCode args byte-identical (`--tools ""`, `--max-turns 1`) when MCP is off; Task 6 only wraps the client and swaps the provider when `Enabled && provider != ClaudeCode`.
- **Version-sensitive spots flagged:** MEAI `MaximumIterationsPerRequest`/`UseFunctionInvocation` and the MCP SDK `McpClient.CreateAsync`/`ListToolsAsync`/`HttpClientTransportOptions` are pinned to the verified package versions with an implementer note in Task 6.
