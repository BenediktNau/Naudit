# Naudit Code-Review-Bot — Implementierungsplan (POC/MVP)

> **Für ausführende Personen / Agenten:** Die Schritte nutzen Checkbox-Syntax (`- [ ]`) zum Abhaken. Arbeite Task für Task von oben nach unten ab. Jeder Task endet mit grünen Tests und einem Commit.

**Goal:** Ein selbstgehosteter .NET-Service, der auf GitLab-Merge-Request-Webhooks reagiert, das MR-Diff von einem über Microsoft.Extensions.AI (MEAI) austauschbaren LLM reviewen lässt und das Ergebnis als **einen** Markdown-Kommentar an den MR zurückschreibt.

**Architecture:** Drei Projekte. `Naudit.Core` enthält Domäne, Orchestrierung (`ReviewService`) und Abstraktionen (`IGitPlatform`) und hängt nur an den MEAI-Abstractions — kennt also keinen konkreten Provider und keine Git-Plattform. `Naudit.Infrastructure` enthält alle SDK-/HTTP-Implementierungen (AI-Provider-Factory + GitLab-Client). `Naudit.Web` ist ein ASP.NET-Minimal-API-Host: nimmt den Webhook an, antwortet sofort `200` und verarbeitet das Review in einem `BackgroundService`. Provider werden allein über Konfiguration gewählt (`IChatClient` aus MEAI), GitHub kommt später als zweite `IGitPlatform`-Implementierung dazu — ohne Core-Änderung.

**Tech Stack:** .NET 10, ASP.NET Core Minimal API, Microsoft.Extensions.AI; Provider-Clients: `OllamaSharp` (Ollama), `Anthropic.SDK` (Claude), `OpenAI` + `Microsoft.Extensions.AI.OpenAI` (deckt OpenAI **und** die OpenAI-kompatible NVIDIA-API für Nemotron ab); Tests mit xUnit + `Microsoft.AspNetCore.Mvc.Testing`.

---

## Designentscheidungen (für den POC fixiert)

- **Plattform:** GitLab zuerst, hinter `IGitPlatform`. GitHub später als 2. Implementierung.
- **AI-Provider:** Anthropic, Ollama, NVIDIA. NVIDIA = OpenAI-kompatibel → kein eigener Adapter, sondern der OpenAI-Client mit `Endpoint=https://integrate.api.nvidia.com/v1` und `Model=nvidia/llama-3.1-nemotron-ultra-253b-v1`.
- **Ausgabe:** Ein zusammenfassender Markdown-Kommentar (kein Inline-/Positions-Mapping).
- **Prompt:** Ein globaler System-Prompt aus der Config (kein `.naudit.yml` pro Repo — YAGNI).
- **Verarbeitung:** Webhook antwortet sofort `200`, Review läuft asynchron im Hintergrund (GitLab-Webhook-Timeout vermeiden).
- **Weggelassen (bewusst):** strukturierte `Finding`-/`ReviewResult`-Typen — das Modell liefert direkt Markdown; Idempotenz/De-Duplizierung wiederholter Events; Diff-Größen-Begrenzung. Alles spätere Erweiterungen.

> **Hinweis zu Fremd-Bibliotheken:** An drei Stellen hängt der exakte Methodenname von der installierten Paketversion ab — sie sind im Plan markiert mit ⚠️ **API-Check**. MEAI ist ab Version 9.x GA; der Plan nutzt die GA-Namen (`GetResponseAsync`, `ChatResponse.Text`, `.AsIChatClient()`). Falls `dotnet build` dort einen fehlenden Member meldet, ist es genau diese Versionsabweichung.

## Dateistruktur (Zielbild)

```
Naudit.sln
src/
  Naudit.Core/
    Models/ReviewRequest.cs          # record ReviewRequest, record CodeChange
    Abstractions/IGitPlatform.cs     # IGitPlatform
    Review/PromptBuilder.cs          # DefaultSystemPrompt + Build(...)
    Review/ReviewOptions.cs          # SystemPrompt
    Review/ReviewService.cs          # Orchestrierung
  Naudit.Infrastructure/
    Ai/AiOptions.cs                  # AiProvider enum + AiOptions
    Ai/AiClientFactory.cs            # Create(AiOptions) -> IChatClient
    Git/GitLab/GitLabOptions.cs      # BaseUrl, Token, WebhookSecret
    Git/GitLab/GitLabDtos.cs         # Webhook- + Changes-DTOs
    Git/GitLab/GitLabWebhook.cs      # ToReviewRequest(payload)
    Git/GitLab/GitLabPlatform.cs     # IGitPlatform-Impl (HTTP)
    DependencyInjection.cs           # AddNauditInfrastructure(config)
  Naudit.Web/
    Program.cs                       # Host, Webhook-Endpoint, Wiring
    ReviewQueue.cs                   # IReviewQueue + ReviewQueue
    ReviewBackgroundService.cs       # BackgroundService
    appsettings.json
tests/
  Naudit.Tests/
    Fakes/FakeChatClient.cs
    Fakes/FakeGitPlatform.cs
    Fakes/StubHttpMessageHandler.cs
    PromptBuilderTests.cs
    ReviewServiceTests.cs
    AiClientFactoryTests.cs
    GitLabWebhookTests.cs
    GitLabPlatformTests.cs
    ReviewQueueTests.cs
    WebhookEndpointTests.cs
```

**Abhängigkeiten:** `Web` → {`Infrastructure`, `Core`}; `Infrastructure` → `Core`; `Core` → nur MEAI-Abstractions.

---

## Task 1: Solution, Projekte, Referenzen, Pakete

**Files:** (alle generiert)

- [ ] **Schritt 1: Solution + Projekte anlegen**

Im Repo-Root (`/home/bnau/workspace/Naudit`):

```bash
dotnet new sln -n Naudit
dotnet new classlib -n Naudit.Core -o src/Naudit.Core -f net10.0
dotnet new classlib -n Naudit.Infrastructure -o src/Naudit.Infrastructure -f net10.0
dotnet new web -n Naudit.Web -o src/Naudit.Web -f net10.0
dotnet new xunit -n Naudit.Tests -o tests/Naudit.Tests -f net10.0
rm src/Naudit.Core/Class1.cs src/Naudit.Infrastructure/Class1.cs
```

- [ ] **Schritt 2: Projekte in die Solution aufnehmen**

```bash
dotnet sln Naudit.sln add src/Naudit.Core/Naudit.Core.csproj src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Web/Naudit.Web.csproj tests/Naudit.Tests/Naudit.Tests.csproj
```

- [ ] **Schritt 3: Projektreferenzen setzen**

```bash
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj reference src/Naudit.Core/Naudit.Core.csproj
dotnet add src/Naudit.Web/Naudit.Web.csproj reference src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Core/Naudit.Core.csproj
dotnet add tests/Naudit.Tests/Naudit.Tests.csproj reference src/Naudit.Core/Naudit.Core.csproj src/Naudit.Infrastructure/Naudit.Infrastructure.csproj src/Naudit.Web/Naudit.Web.csproj
```

- [ ] **Schritt 4: NuGet-Pakete hinzufügen**

```bash
# Core: nur die Abstraktionen
dotnet add src/Naudit.Core/Naudit.Core.csproj package Microsoft.Extensions.AI.Abstractions

# Infrastructure: MEAI + Provider-SDKs + DI/HTTP/Config-Helfer
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Microsoft.Extensions.AI
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Microsoft.Extensions.AI.OpenAI
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package OpenAI
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package OllamaSharp
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Anthropic.SDK
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Microsoft.Extensions.Http
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Microsoft.Extensions.Options.ConfigurationExtensions
dotnet add src/Naudit.Infrastructure/Naudit.Infrastructure.csproj package Microsoft.Extensions.Configuration.Binder

# Tests: WebApplicationFactory für den Endpoint-Test
dotnet add tests/Naudit.Tests/Naudit.Tests.csproj package Microsoft.AspNetCore.Mvc.Testing
```

> ⚠️ **API-Check:** Falls `Microsoft.Extensions.AI.OpenAI` als reines Stable-Paket (noch) nicht auflösbar ist, denselben Befehl mit `--prerelease` ausführen.

- [ ] **Schritt 5: Build prüfen (leeres Gerüst kompiliert)**

Run: `dotnet build Naudit.sln`
Expected: `Build succeeded` (0 Errors).

- [ ] **Schritt 6: Commit**

```bash
git checkout -b feat/naudit-poc
git add -A
git commit -m "chore: scaffold Naudit solution (Core/Infrastructure/Web/Tests)"
```

---

## Task 2: Domänenmodelle + IGitPlatform (Core)

**Files:**
- Create: `src/Naudit.Core/Models/ReviewRequest.cs`
- Create: `src/Naudit.Core/Abstractions/IGitPlatform.cs`

- [ ] **Schritt 1: Modelle schreiben**

`src/Naudit.Core/Models/ReviewRequest.cs`:

```csharp
namespace Naudit.Core.Models;

/// <summary>Identifiziert den zu reviewenden Merge Request.</summary>
public sealed record ReviewRequest(string ProjectId, long MergeRequestIid, string Title);

/// <summary>Eine geänderte Datei mit ihrem unified diff.</summary>
public sealed record CodeChange(string FilePath, string Diff);
```

- [ ] **Schritt 2: Plattform-Abstraktion schreiben**

`src/Naudit.Core/Abstractions/IGitPlatform.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab zuerst; GitHub später als zweite Implementierung.</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);
    Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default);
}
```

- [ ] **Schritt 3: Build prüfen**

Run: `dotnet build src/Naudit.Core/Naudit.Core.csproj`
Expected: `Build succeeded`.

- [ ] **Schritt 4: Commit**

```bash
git add src/Naudit.Core
git commit -m "feat(core): add domain models and IGitPlatform abstraction"
```

---

## Task 3: PromptBuilder (Core, TDD)

**Files:**
- Create: `src/Naudit.Core/Review/PromptBuilder.cs`
- Test: `tests/Naudit.Tests/PromptBuilderTests.cs`

- [ ] **Schritt 1: Failing Test schreiben**

`tests/Naudit.Tests/PromptBuilderTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PromptBuilderTests
{
    [Fact]
    public void Build_putsSystemPromptFirst_andEmbedsDiffsAndPaths()
    {
        var request = new ReviewRequest("1", 42, "Add feature X");
        var changes = new[]
        {
            new CodeChange("src/Foo.cs", "@@ -1 +1 @@\n-old\n+new"),
            new CodeChange("src/Bar.cs", "@@ -2 +2 @@\n+added"),
        };

        var messages = PromptBuilder.Build("SYSTEM-PROMPT-MARKER", request, changes);

        Assert.Equal(2, messages.Count);
        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal("SYSTEM-PROMPT-MARKER", messages[0].Text);
        Assert.Equal(ChatRole.User, messages[1].Role);
        Assert.Contains("Add feature X", messages[1].Text);
        Assert.Contains("src/Foo.cs", messages[1].Text);
        Assert.Contains("+new", messages[1].Text);
        Assert.Contains("src/Bar.cs", messages[1].Text);
    }
}
```

- [ ] **Schritt 2: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`
Expected: FAIL (Kompilierfehler: `PromptBuilder` existiert nicht).

- [ ] **Schritt 3: Minimale Implementierung schreiben**

`src/Naudit.Core/Review/PromptBuilder.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public static class PromptBuilder
{
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Focus on correctness bugs, security issues and clear maintainability problems. " +
        "Be concise. Answer in GitHub-flavored Markdown: a one-line summary followed by a bullet list of findings. " +
        "If there are no significant issues, say so briefly.";

    public static IList<ChatMessage> Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge Request: {request.Title}");
        foreach (var change in changes)
        {
            sb.AppendLine();
            sb.AppendLine($"## File: {change.FilePath}");
            sb.AppendLine("```diff");
            sb.AppendLine(change.Diff);
            sb.AppendLine("```");
        }

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }
}
```

- [ ] **Schritt 4: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`
Expected: PASS (1 Test).

- [ ] **Schritt 5: Commit**

```bash
git add src/Naudit.Core/Review/PromptBuilder.cs tests/Naudit.Tests/PromptBuilderTests.cs
git commit -m "feat(core): add PromptBuilder with default review system prompt"
```

---

## Task 4: ReviewService + ReviewOptions (Core, TDD)

**Files:**
- Create: `src/Naudit.Core/Review/ReviewOptions.cs`
- Create: `src/Naudit.Core/Review/ReviewService.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeChatClient.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

- [ ] **Schritt 1: Fakes schreiben**

`tests/Naudit.Tests/Fakes/FakeChatClient.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Naudit.Tests.Fakes;

internal sealed class FakeChatClient(string responseText) : IChatClient
{
    public List<ChatMessage>? LastMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
```

> ⚠️ **API-Check:** MEAI-GA-Signaturen sind `GetResponseAsync` → `Task<ChatResponse>` und `ChatResponse(ChatMessage)`. Bei Preview-Paketen hießen sie `CompleteAsync`/`ChatCompletion`.

`tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeGitPlatform(IReadOnlyList<CodeChange> changes) : IGitPlatform
{
    public string? PostedMarkdown { get; private set; }
    public int PostCallCount { get; private set; }

    public Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(changes);

    public Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        PostedMarkdown = markdown;
        PostCallCount++;
        return Task.CompletedTask;
    }
}
```

- [ ] **Schritt 2: Failing Test schreiben**

`tests/Naudit.Tests/ReviewServiceTests.cs`:

```csharp
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ReviewServiceTests
{
    private static readonly ReviewRequest Request = new("1", 42, "Title");

    [Fact]
    public async Task ReviewAsync_postsModelOutput_asSummary()
    {
        var chat = new FakeChatClient("## Review\n- looks fine");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });

        await service.ReviewAsync(Request);

        Assert.Equal("## Review\n- looks fine", git.PostedMarkdown);
        Assert.Equal("SYS", chat.LastMessages![0].Text);
    }

    [Fact]
    public async Task ReviewAsync_withNoChanges_postsNothing()
    {
        var chat = new FakeChatClient("unused");
        var git = new FakeGitPlatform([]);
        var service = new ReviewService(chat, git, new ReviewOptions());

        await service.ReviewAsync(Request);

        Assert.Equal(0, git.PostCallCount);
    }
}
```

- [ ] **Schritt 3: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: FAIL (`ReviewService`/`ReviewOptions` existieren nicht).

- [ ] **Schritt 4: Implementierung schreiben**

`src/Naudit.Core/Review/ReviewOptions.cs`:

```csharp
namespace Naudit.Core.Review;

public sealed class ReviewOptions
{
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;
}
```

`src/Naudit.Core/Review/ReviewService.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(IChatClient chatClient, IGitPlatform gitPlatform, ReviewOptions options)
{
    public async Task ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return;

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes);
        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        await gitPlatform.PostSummaryAsync(request, response.Text, ct);
    }
}
```

> ⚠️ **API-Check:** `response.Text` ist die GA-Eigenschaft von `ChatResponse` (Verkettung des Antworttextes).

- [ ] **Schritt 5: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: PASS (2 Tests).

- [ ] **Schritt 6: Commit**

```bash
git add src/Naudit.Core/Review tests/Naudit.Tests
git commit -m "feat(core): add ReviewService orchestration with fakes-based tests"
```

---

## Task 5: AI-Provider-Factory (Infrastructure, TDD)

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/AiOptions.cs`
- Create: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`
- Test: `tests/Naudit.Tests/AiClientFactoryTests.cs`

- [ ] **Schritt 1: Options-Typ schreiben** (wird auch vom Test gebraucht)

`src/Naudit.Infrastructure/Ai/AiOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Ai;

public enum AiProvider { Anthropic, Ollama, OpenAICompatible }

public sealed class AiOptions
{
    public AiProvider Provider { get; set; } = AiProvider.Ollama;
    public string Model { get; set; } = "";
    public string? ApiKey { get; set; }
    /// <summary>Ollama-Base-URL oder Base-URL eines OpenAI-kompatiblen Dienstes (z. B. NVIDIA).</summary>
    public string? Endpoint { get; set; }
}
```

- [ ] **Schritt 2: Failing Test schreiben**

`tests/Naudit.Tests/AiClientFactoryTests.cs`:

```csharp
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using OllamaSharp;
using Xunit;

namespace Naudit.Tests;

public class AiClientFactoryTests
{
    [Fact]
    public void Create_ollama_returnsChatClient()
    {
        var client = AiClientFactory.Create(new AiOptions
        {
            Provider = AiProvider.Ollama,
            Model = "llama3.1",
            Endpoint = "http://localhost:11434",
        });

        Assert.IsType<OllamaApiClient>(client);
    }

    [Fact]
    public void Create_anthropic_withoutApiKey_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            AiClientFactory.Create(new AiOptions { Provider = AiProvider.Anthropic, Model = "claude-sonnet-4-6" }));
        Assert.Contains("ApiKey", ex.Message);
    }

    [Fact]
    public void Create_openAICompatible_withoutApiKey_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            AiClientFactory.Create(new AiOptions { Provider = AiProvider.OpenAICompatible, Model = "gpt-4o" }));
    }
}
```

- [ ] **Schritt 3: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter AiClientFactoryTests`
Expected: FAIL (`AiClientFactory` existiert nicht).

- [ ] **Schritt 4: Implementierung schreiben**

`src/Naudit.Infrastructure/Ai/AiClientFactory.cs`:

```csharp
using System.ClientModel;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace Naudit.Infrastructure.Ai;

public static class AiClientFactory
{
    public static IChatClient Create(AiOptions options)
    {
        switch (options.Provider)
        {
            case AiProvider.Ollama:
                var baseUrl = string.IsNullOrWhiteSpace(options.Endpoint) ? "http://localhost:11434" : options.Endpoint;
                return new OllamaApiClient(new Uri(baseUrl), options.Model);

            case AiProvider.Anthropic:
                RequireApiKey(options, "Anthropic");
                return new Anthropic.SDK.AnthropicClient(options.ApiKey!).Messages;

            case AiProvider.OpenAICompatible:
                RequireApiKey(options, "OpenAICompatible");
                return CreateOpenAICompatible(options);

            default:
                throw new ArgumentOutOfRangeException(nameof(options), options.Provider, "Unknown AI provider.");
        }
    }

    private static IChatClient CreateOpenAICompatible(AiOptions options)
    {
        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            clientOptions.Endpoint = new Uri(options.Endpoint);

        var client = new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions);
        return client.GetChatClient(options.Model).AsIChatClient();
    }

    private static void RequireApiKey(AiOptions options, string provider)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
            throw new ArgumentException($"ApiKey is required for the {provider} provider.", nameof(options));
    }
}
```

> ⚠️ **API-Check:** Drei versionsabhängige Stellen — (1) `AnthropicClient(apiKey).Messages` ist bei `Anthropic.SDK` der `IChatClient`; (2) `client.GetChatClient(model).AsIChatClient()` ist die MEAI-GA-Brücke aus `Microsoft.Extensions.AI.OpenAI` (Preview hieß `.AsChatClient()`); (3) `new OllamaApiClient(Uri, model)` aus `OllamaSharp`. Bei Build-Fehler hier zuerst die installierte Paketversion prüfen.

- [ ] **Schritt 5: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter AiClientFactoryTests`
Expected: PASS (3 Tests).

- [ ] **Schritt 6: Commit**

```bash
git add src/Naudit.Infrastructure/Ai tests/Naudit.Tests/AiClientFactoryTests.cs
git commit -m "feat(infra): add MEAI provider factory (Ollama/Anthropic/OpenAI-compatible)"
```

---

## Task 6: GitLab-DTOs + Webhook-Mapping (Infrastructure, TDD)

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs`
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs`
- Test: `tests/Naudit.Tests/GitLabWebhookTests.cs`

- [ ] **Schritt 1: DTOs schreiben** (auch vom Test/Endpoint genutzt)

`src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Naudit.Infrastructure.Git.GitLab;

public sealed class GitLabWebhookPayload
{
    [JsonPropertyName("object_kind")] public string? ObjectKind { get; set; }
    [JsonPropertyName("project")] public GitLabProject? Project { get; set; }
    [JsonPropertyName("object_attributes")] public GitLabMergeRequestAttributes? ObjectAttributes { get; set; }
}

public sealed class GitLabProject
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class GitLabMergeRequestAttributes
{
    [JsonPropertyName("iid")] public long Iid { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("action")] public string? Action { get; set; }
}

public sealed class GitLabChangesResponse
{
    [JsonPropertyName("changes")] public List<GitLabChange>? Changes { get; set; }
}

public sealed class GitLabChange
{
    [JsonPropertyName("new_path")] public string NewPath { get; set; } = "";
    [JsonPropertyName("diff")] public string Diff { get; set; } = "";
}
```

- [ ] **Schritt 2: Failing Test schreiben**

`tests/Naudit.Tests/GitLabWebhookTests.cs`:

```csharp
using System.Text.Json;
using Naudit.Infrastructure.Git.GitLab;
using Xunit;

namespace Naudit.Tests;

public class GitLabWebhookTests
{
    private const string MergeRequestEvent = """
    {
      "object_kind": "merge_request",
      "project": { "id": 7 },
      "object_attributes": { "iid": 42, "title": "Add feature X", "action": "open" }
    }
    """;

    [Fact]
    public void ToReviewRequest_mapsMergeRequestEvent()
    {
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(MergeRequestEvent)!;

        var request = GitLabWebhook.ToReviewRequest(payload);

        Assert.NotNull(request);
        Assert.Equal("7", request!.ProjectId);
        Assert.Equal(42, request.MergeRequestIid);
        Assert.Equal("Add feature X", request.Title);
    }

    [Fact]
    public void ToReviewRequest_ignoresNonMergeRequestEvents()
    {
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>("""{ "object_kind": "push" }""")!;
        Assert.Null(GitLabWebhook.ToReviewRequest(payload));
    }

    [Fact]
    public void ToReviewRequest_ignoresNonReviewableActions()
    {
        var json = """{ "object_kind": "merge_request", "project": { "id": 1 }, "object_attributes": { "iid": 1, "title": "x", "action": "close" } }""";
        var payload = JsonSerializer.Deserialize<GitLabWebhookPayload>(json)!;
        Assert.Null(GitLabWebhook.ToReviewRequest(payload));
    }
}
```

- [ ] **Schritt 3: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabWebhookTests`
Expected: FAIL (`GitLabWebhook` existiert nicht).

- [ ] **Schritt 4: Implementierung schreiben**

`src/Naudit.Infrastructure/Git/GitLab/GitLabWebhook.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

public static class GitLabWebhook
{
    private static readonly string[] ReviewableActions = ["open", "reopen", "update"];

    /// <summary>Mappt ein GitLab-Webhook-Payload auf einen ReviewRequest, oder null wenn nichts zu reviewen ist.</summary>
    public static ReviewRequest? ToReviewRequest(GitLabWebhookPayload payload)
    {
        if (payload.ObjectKind != "merge_request")
            return null;

        var attrs = payload.ObjectAttributes;
        if (attrs is null || payload.Project is null)
            return null;

        if (attrs.Action is null || !ReviewableActions.Contains(attrs.Action))
            return null;

        return new ReviewRequest(payload.Project.Id.ToString(), attrs.Iid, attrs.Title ?? "");
    }
}
```

- [ ] **Schritt 5: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabWebhookTests`
Expected: PASS (3 Tests).

- [ ] **Schritt 6: Commit**

```bash
git add src/Naudit.Infrastructure/Git tests/Naudit.Tests/GitLabWebhookTests.cs
git commit -m "feat(infra): add GitLab webhook DTOs and ReviewRequest mapping"
```

---

## Task 7: GitLabPlatform HTTP-Client (Infrastructure, TDD)

**Files:**
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabOptions.cs`
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`
- Create: `tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs`
- Test: `tests/Naudit.Tests/GitLabPlatformTests.cs`

- [ ] **Schritt 1: Options + HTTP-Stub schreiben**

`src/Naudit.Infrastructure/Git/GitLab/GitLabOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Git.GitLab;

public sealed class GitLabOptions
{
    public string BaseUrl { get; set; } = "";       // z. B. https://gitlab.example.com
    public string Token { get; set; } = "";          // Personal/Project Access Token mit api-Scope
    public string WebhookSecret { get; set; } = "";  // Vergleich gegen Header X-Gitlab-Token
}
```

`tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs`:

```csharp
namespace Naudit.Tests.Fakes;

internal sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastRequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (request.Content is not null)
            LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
        return responder(request);
    }
}
```

- [ ] **Schritt 2: Failing Test schreiben**

`tests/Naudit.Tests/GitLabPlatformTests.cs`:

```csharp
using System.Net;
using System.Text;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitLabPlatformTests
{
    private static readonly ReviewRequest Request = new("7", 42, "Title");

    private static HttpClient ClientReturning(HttpStatusCode status, string json, StubHttpMessageHandler? capture = null)
    {
        var handler = capture ?? new StubHttpMessageHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });
        return new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.com/") };
    }

    [Fact]
    public async Task GetChangesAsync_mapsChangesFromApi()
    {
        const string json = """{ "changes": [ { "new_path": "src/Foo.cs", "diff": "@@ +1 @@\n+x" } ] }""";
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.OK, json));

        var changes = await platform.GetChangesAsync(Request);

        var change = Assert.Single(changes);
        Assert.Equal("src/Foo.cs", change.FilePath);
        Assert.Contains("+x", change.Diff);
    }

    [Fact]
    public async Task PostSummaryAsync_postsNoteWithBody()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var platform = new GitLabPlatform(ClientReturning(HttpStatusCode.Created, "", capture));

        await platform.PostSummaryAsync(Request, "## Naudit Review");

        Assert.Equal(HttpMethod.Post, capture.LastRequest!.Method);
        Assert.Contains("/merge_requests/42/notes", capture.LastRequest.RequestUri!.ToString());
        Assert.Contains("Naudit Review", capture.LastRequestBody!);
    }
}
```

- [ ] **Schritt 3: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabPlatformTests`
Expected: FAIL (`GitLabPlatform` existiert nicht).

- [ ] **Schritt 4: Implementierung schreiben**

`src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`:

```csharp
using System.Net.Http.Json;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>IGitPlatform-Implementierung für GitLab. BaseAddress + PRIVATE-TOKEN kommen vom typed HttpClient.</summary>
public sealed class GitLabPlatform(HttpClient http) : IGitPlatform
{
    public async Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/changes";
        var response = await http.GetFromJsonAsync<GitLabChangesResponse>(url, ct);
        if (response?.Changes is null)
            return [];

        return response.Changes
            .Select(c => new CodeChange(c.NewPath, c.Diff))
            .ToList();
    }

    public async Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/notes";
        var response = await http.PostAsJsonAsync(url, new { body = markdown }, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

> Hinweis: GitLabs `/changes`-Endpoint ist in neueren Versionen zugunsten von `/diffs` als „deprecated" markiert, funktioniert aber weiterhin. Falls deine GitLab-Instanz `/changes` nicht mehr liefert, URL auf `/diffs` umstellen (gleiche Feldnamen `new_path`/`diff`).

- [ ] **Schritt 5: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter GitLabPlatformTests`
Expected: PASS (2 Tests).

- [ ] **Schritt 6: Commit**

```bash
git add src/Naudit.Infrastructure/Git tests/Naudit.Tests
git commit -m "feat(infra): add GitLab HTTP platform client"
```

---

## Task 8: DI-Komposition AddNauditInfrastructure (Infrastructure)

**Files:**
- Create: `src/Naudit.Infrastructure/DependencyInjection.cs`

- [ ] **Schritt 1: Extension schreiben**

`src/Naudit.Infrastructure/DependencyInjection.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Naudit.Core.Abstractions;
using Naudit.Core.Review;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Git.GitLab;

namespace Naudit.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNauditInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // AI-Provider: aus Config gewählt, hinter IChatClient (austauschbar via appsettings).
        var aiOptions = configuration.GetSection("Naudit:Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<IChatClient>(_ => AiClientFactory.Create(aiOptions));

        // Review-Prompt: leerer Config-Wert -> Default-Prompt.
        var reviewOptions = configuration.GetSection("Naudit:Review").Get<ReviewOptions>() ?? new ReviewOptions();
        if (string.IsNullOrWhiteSpace(reviewOptions.SystemPrompt))
            reviewOptions.SystemPrompt = PromptBuilder.DefaultSystemPrompt;
        services.AddSingleton(reviewOptions);

        // GitLab: typed HttpClient mit BaseAddress + Token.
        services.Configure<GitLabOptions>(configuration.GetSection("Naudit:GitLab"));
        services.AddHttpClient<IGitPlatform, GitLabPlatform>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
            http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
            http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", opt.Token);
        });

        services.AddScoped<ReviewService>();
        return services;
    }
}
```

- [ ] **Schritt 2: Build prüfen**

Run: `dotnet build src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`
Expected: `Build succeeded`.

- [ ] **Schritt 3: Commit**

```bash
git add src/Naudit.Infrastructure/DependencyInjection.cs
git commit -m "feat(infra): add AddNauditInfrastructure DI composition"
```

---

## Task 9: Review-Queue + BackgroundService (Web, TDD)

**Files:**
- Create: `src/Naudit.Web/ReviewQueue.cs`
- Create: `src/Naudit.Web/ReviewBackgroundService.cs`
- Test: `tests/Naudit.Tests/ReviewQueueTests.cs`

- [ ] **Schritt 1: Failing Test schreiben**

`tests/Naudit.Tests/ReviewQueueTests.cs`:

```csharp
using Naudit.Core.Models;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class ReviewQueueTests
{
    [Fact]
    public async Task EnqueuedRequest_isDequeued()
    {
        var queue = new ReviewQueue();
        var request = new ReviewRequest("1", 42, "Test");
        await queue.EnqueueAsync(request);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        ReviewRequest? dequeued = null;
        await foreach (var item in queue.DequeueAllAsync(cts.Token))
        {
            dequeued = item;
            break;
        }

        Assert.Equal(request, dequeued);
    }
}
```

- [ ] **Schritt 2: Test laufen lassen — muss fehlschlagen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewQueueTests`
Expected: FAIL (`ReviewQueue` existiert nicht).

- [ ] **Schritt 3: Implementierung schreiben**

`src/Naudit.Web/ReviewQueue.cs`:

```csharp
using System.Threading.Channels;
using Naudit.Core.Models;

namespace Naudit.Web;

public interface IReviewQueue
{
    ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct = default);
    IAsyncEnumerable<ReviewRequest> DequeueAllAsync(CancellationToken ct);
}

public sealed class ReviewQueue : IReviewQueue
{
    private readonly Channel<ReviewRequest> _channel = Channel.CreateUnbounded<ReviewRequest>();

    public ValueTask EnqueueAsync(ReviewRequest request, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<ReviewRequest> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
```

`src/Naudit.Web/ReviewBackgroundService.cs`:

```csharp
using Naudit.Core.Review;

namespace Naudit.Web;

public sealed class ReviewBackgroundService(
    IReviewQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ReviewBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.DequeueAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var reviewService = scope.ServiceProvider.GetRequiredService<ReviewService>();
                await reviewService.ReviewAsync(request, stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Review failed for MR {Iid}", request.MergeRequestIid);
            }
        }
    }
}
```

- [ ] **Schritt 4: Test laufen lassen — muss bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewQueueTests`
Expected: PASS (1 Test).

> Falls der Test-Build meckert, dass `Naudit.Web`-Typen nicht sichtbar sind: das `web`-SDK erzeugt eine implizite `Program`-Klasse, die Klassen darin sind aber `public` — kein Problem. Die Referenz auf das Web-Projekt steht bereits (Task 1, Schritt 3).

- [ ] **Schritt 5: Commit**

```bash
git add src/Naudit.Web tests/Naudit.Tests/ReviewQueueTests.cs
git commit -m "feat(web): add in-memory review queue and background worker"
```

---

## Task 10: Host, Webhook-Endpoint, Config (Web)

**Files:**
- Modify (ersetzen): `src/Naudit.Web/Program.cs`
- Modify (ersetzen): `src/Naudit.Web/appsettings.json`
- Test: `tests/Naudit.Tests/WebhookEndpointTests.cs`

- [ ] **Schritt 1: Program.cs ersetzen**

`src/Naudit.Web/Program.cs` (kompletter Inhalt):

```csharp
using Microsoft.Extensions.Options;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNauditInfrastructure(builder.Configuration);
builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
builder.Services.AddHostedService<ReviewBackgroundService>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok("healthy"));

app.MapPost("/webhook/gitlab", async (HttpContext context, IReviewQueue queue, IOptions<GitLabOptions> gitLabOptions) =>
{
    var secret = gitLabOptions.Value.WebhookSecret;
    var token = context.Request.Headers["X-Gitlab-Token"].ToString();
    if (string.IsNullOrEmpty(secret) || token != secret)
        return Results.Unauthorized();

    var payload = await context.Request.ReadFromJsonAsync<GitLabWebhookPayload>();
    if (payload is null)
        return Results.Ok();

    var request = GitLabWebhook.ToReviewRequest(payload);
    if (request is null)
        return Results.Ok(); // kein MR-Event oder keine reviewbare Aktion

    await queue.EnqueueAsync(request);
    return Results.Ok();
});

app.Run();

// Sichtbar machen für WebApplicationFactory<Program> im Testprojekt.
public partial class Program { }
```

- [ ] **Schritt 2: appsettings.json ersetzen**

`src/Naudit.Web/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Naudit": {
    "Ai": {
      "Provider": "Ollama",
      "Model": "llama3.1",
      "Endpoint": "http://localhost:11434",
      "ApiKey": ""
    },
    "GitLab": {
      "BaseUrl": "https://gitlab.example.com",
      "Token": "",
      "WebhookSecret": ""
    },
    "Review": {
      "SystemPrompt": ""
    }
  }
}
```

> Secrets (`GitLab:Token`, `GitLab:WebhookSecret`, `Ai:ApiKey`) **nicht** hier ablegen, sondern in Task 11 per User-Secrets/Umgebungsvariablen setzen.

- [ ] **Schritt 3: Failing Test schreiben**

`tests/Naudit.Tests/WebhookEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Naudit.Tests;

public class WebhookEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public WebhookEndpointTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Webhook_withWrongToken_returnsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/webhook/gitlab", new { object_kind = "merge_request" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_withValidToken_andNonMergeRequestEvent_returnsOk()
    {
        var client = _factory
            .WithWebHostBuilder(b => b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret"))
            .CreateClient();

        var message = new HttpRequestMessage(HttpMethod.Post, "/webhook/gitlab")
        {
            Content = JsonContent.Create(new { object_kind = "push" }),
        };
        message.Headers.Add("X-Gitlab-Token", "test-secret");

        var response = await client.SendAsync(message);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

> Beide Tests treffen bewusst **keinen** AI-Provider und **keine** GitLab-API: Der 401-Pfad endet vor jeder Verarbeitung; der `push`-Pfad wird von `ToReviewRequest` verworfen, bevor etwas in die Queue geht. Der echte End-to-End-Pfad wird in Task 11 manuell getestet.

- [ ] **Schritt 4: Test laufen lassen — muss fehlschlagen, dann (nach Schritt 1/2) bestehen**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WebhookEndpointTests`
Expected: Nach Schritt 1–3 PASS (2 Tests). Falls `Program` nicht gefunden wird: prüfen, dass die Zeile `public partial class Program { }` am Ende von `Program.cs` steht.

- [ ] **Schritt 5: Gesamte Suite laufen lassen**

Run: `dotnet test Naudit.sln`
Expected: PASS (insgesamt 14 Tests: 1 PromptBuilder, 2 ReviewService, 3 AiClientFactory, 3 GitLabWebhook, 2 GitLabPlatform, 1 ReviewQueue, 2 WebhookEndpoint).

- [ ] **Schritt 6: Commit**

```bash
git add src/Naudit.Web/Program.cs src/Naudit.Web/appsettings.json tests/Naudit.Tests/WebhookEndpointTests.cs
git commit -m "feat(web): add GitLab webhook endpoint with token auth and async dispatch"
```

---

## Task 11: Manuelles End-to-End-Smoke-Test + README

**Files:**
- Modify: `README.md`

Dieser Task hat keinen automatisierten Test — er verdrahtet den Bot gegen echtes Ollama + echtes GitLab.

- [ ] **Schritt 1: Lokales LLM bereitstellen (Ollama)**

```bash
# Ollama installieren (https://ollama.com), dann Modell laden:
ollama pull llama3.1
ollama serve   # läuft auf http://localhost:11434
```

- [ ] **Schritt 2: Secrets setzen (User-Secrets)**

```bash
dotnet user-secrets init --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:BaseUrl" "https://DEINE-GITLAB-INSTANZ" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token" "DEIN_GITLAB_TOKEN_MIT_API_SCOPE" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "EIN_SELBSTGEWAEHLTES_GEHEIMNIS" --project src/Naudit.Web
```

- [ ] **Schritt 3: Service starten**

Run: `dotnet run --project src/Naudit.Web`
Expected: Log zeigt `Now listening on: http://localhost:5xxx`. `curl http://localhost:5xxx/health` → `healthy`.

- [ ] **Schritt 4: Erreichbarkeit für GitLab herstellen**

GitLab muss den Service erreichen. Für einen lokalen Test ein Tunnel, z. B.:

```bash
ngrok http 5xxx   # öffentliche URL notieren -> https://<id>.ngrok.io
```

- [ ] **Schritt 5: Webhook in GitLab anlegen**

Im Ziel-Projekt → *Settings → Webhooks*:
- URL: `https://<id>.ngrok.io/webhook/gitlab`
- Secret Token: dasselbe Geheimnis wie in Schritt 2 (`WebhookSecret`)
- Trigger: nur **Merge request events** aktivieren
- *Add webhook*, dann *Test → Merge request events* (oder echten MR öffnen)

- [ ] **Schritt 6: Ergebnis prüfen**

Erwartung: Im MR erscheint ein Naudit-Kommentar mit der Review-Zusammenfassung. Bei Fehlern: Service-Logs prüfen (`Review failed for MR ...`).

- [ ] **Schritt 7: Provider gegen Anthropic / NVIDIA tauschen (optional verifizieren)**

Nur Config ändern, kein Code. Per User-Secrets, dann Neustart:

```bash
# Anthropic (Claude)
dotnet user-secrets set "Naudit:Ai:Provider" "Anthropic" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model" "claude-sonnet-4-6" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey" "DEIN_ANTHROPIC_KEY" --project src/Naudit.Web

# NVIDIA (Nemotron Ultra, OpenAI-kompatibel)
dotnet user-secrets set "Naudit:Ai:Provider" "OpenAICompatible" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "https://integrate.api.nvidia.com/v1" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model" "nvidia/llama-3.1-nemotron-ultra-253b-v1" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey" "DEIN_NVIDIA_KEY" --project src/Naudit.Web
```

Erwartung: Identisches Verhalten, nur anderes Modell — Beleg, dass Provider austauschbar sind.

- [ ] **Schritt 8: README aktualisieren**

Ergänze `README.md` um: Voraussetzungen (.NET 10, optional Ollama), `dotnet test`, `dotnet run --project src/Naudit.Web`, Konfiguration der drei Provider, GitLab-Webhook-Setup. (Inhalt aus den Schritten oben übernehmen.)

- [ ] **Schritt 9: Commit + Branch zusammenführen**

```bash
git add README.md
git commit -m "docs: add setup and provider configuration to README"
# danach nach Wunsch: PR öffnen oder in main mergen
```

---

## Selbstreview-Notiz (Plan-Abdeckung)

- **GitLab zuerst, IGitPlatform-Abstraktion:** Task 2 (Interface), Task 7 (Impl). GitHub später = neue Klasse, kein Core-Eingriff. ✅
- **Provider austauschbar via MEAI/Config:** Task 5 (Factory), Task 8 (Config-Auswahl), Task 11/7 (Verifikation). ✅
- **Anthropic + Ollama + NVIDIA:** Task 5 deckt alle drei (NVIDIA = OpenAI-kompatibel). ✅
- **Ein Summary-Kommentar:** Task 4 (`PostSummaryAsync`), Task 7 (Notes-API). ✅
- **Globaler Prompt aus Config:** Task 3/4 (`ReviewOptions`), Task 8 (Binding + Default-Fallback). ✅
- **Async-Webhook, sofort 200:** Task 9 (Queue/Worker), Task 10 (Endpoint enqueued + Ok). ✅
- **Offene Punkte (bewusst nicht im MVP):** Inline-Kommentare, Idempotenz, Diff-Truncation, per-Repo-Regeln. Als spätere Tasks.
