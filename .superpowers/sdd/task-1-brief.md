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
        IReviewToolProvider? toolProvider = null)
        => new(chat, git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector(),
            new FakeReviewAuditSink(),
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
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
```

with:

```csharp
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // MCP-Tools (leer ⇒ Feature aus): identischer Single-Shot. Nicht-leer ⇒ agentischer Loop
        // über den Function-Invocation-Wrapper des Clients (Infrastructure).
        var tools = await toolProvider.GetToolsAsync(request, ct);
        if (tools.Count > 0)
            chatOptions.Tools = [.. tools];

        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);
```

- [ ] **Step 7: Register the default in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, immediately after the `services.AddSingleton(aiOptions);` line (which follows the `AddSingleton<IChatClient>` registration), add:

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

