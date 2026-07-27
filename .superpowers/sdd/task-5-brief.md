### Task 5: `DastAnalyzer` — orchestration happy path

**Files:**
- Create: `src/Naudit.Infrastructure/Dast/DastProbePrompt.cs`, `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`
- Test: `tests/Naudit.Tests/DastAnalyzerTests.cs`

**Interfaces:**
- Consumes: `IAppRunner.RunAsync` (PR 1), `DastProbeSession` (Task 4), `IChatClient` (global), `DastOptions`, `IReviewWorkspace`, `FakeChatClient`, `FakeDockerClient`, and a fake `IAppRunner`.
- Produces: `DastAnalyzer : ISastAnalyzer` (`Name => "dast"`, `AnalyzeAsync(workspace, changes, ct) -> IReadOnlyList<ScanFinding>`). Emits `ScanFinding(Tool: "dast", Category: FindingCategory.Dast, …)` with the endpoint in `Message`, `FilePath`/`Line` null.

- [ ] **Step 1: Write the probing prompt + JSON contract**

Create `src/Naudit.Infrastructure/Dast/DastProbePrompt.cs`:

```csharp
namespace Naudit.Infrastructure.Dast;

/// <summary>System-Prompt für den agentischen Probing-Lauf. „Playwright ist die Hand, nicht das Hirn":
/// der Browser navigiert, das LLM beurteilt. Grounding-Schritt ⇒ non-JSON ist „keine Funde", nie ein
/// fail-closed-Abbruch.</summary>
public static class DastProbePrompt
{
    public static string System(string appUrl, int maxSteps) =>
        $$"""
        You are a security probe driving a headless browser (Playwright tools) against a running
        web app at {{appUrl}}. Explore reachable pages and forms and look for evidence of concrete
        vulnerabilities: reflected/stored XSS, obvious injection, missing auth on sensitive routes,
        open redirects, sensitive data in responses. Use at most {{maxSteps}} tool calls; be frugal.

        You are grounding a code review, not producing a final verdict. When done, respond with ONLY
        a JSON object, no prose:
        {"findings":[{"severity":"High|Medium|Low","endpoint":"<url or route>","summary":"<one line>"}]}
        If you found nothing, respond exactly {"findings":[]}.
        """;
}
```

- [ ] **Step 2: Write the failing test**

Create `tests/Naudit.Tests/DastAnalyzerTests.cs`. The `FakeChatClient` must return the JSON contract; a fake `IAppRunner` returns a `RunningApp`; the probe session is exercised via `FakeDockerClient` (no live MCP — so the analyzer must tolerate a probe-session start failure as fail-open, tested in Task 6; here we test the path where the model answers with findings). To make the happy path deterministic without a live MCP peer, inject the probe-session factory as a delegate so the test supplies tools directly:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Dast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DastAnalyzerTests
{
    private const string Project = "acme/shop";

    private sealed class Ws(string root) : IReviewWorkspace
    {
        public string RootPath => root;
        public string ProjectId => Project;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static DastOptions Options() => new() { Enabled = true, Projects = { Project } };

    [Fact]
    public async Task Analyze_mapsModelJson_toDastFindings()
    {
        var app = new FakeAppRunner(); // RunAsync -> non-null RunningApp
        var chat = new FakeChatClient(
            "{\"findings\":[{\"severity\":\"High\",\"endpoint\":\"/search?q=\",\"summary\":\"Reflected XSS\"}]}");
        var analyzer = new DastAnalyzer(app, Options(), chat, new FakeDockerClient(),
            NullLoggerFactory.Instance, probeToolsOverride: []);   // leere Toolliste ⇒ kein echter MCP

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("dast", f.Tool);
        Assert.Equal(FindingCategory.Dast, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains("/search?q=", f.Message);
        Assert.Contains("Reflected XSS", f.Message);
        Assert.Null(f.FilePath);
        Assert.True(app.Disposed);   // Teardown lief
    }

    [Fact]
    public async Task Analyze_notAllowlisted_returnsEmpty_withoutRunning()
    {
        var app = new FakeAppRunner();
        var opts = Options(); opts.Projects.Clear();
        var analyzer = new DastAnalyzer(app, opts, new FakeChatClient("{\"findings\":[]}"),
            new FakeDockerClient(), NullLoggerFactory.Instance, probeToolsOverride: []);

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
        Assert.False(app.RunCalled);
    }
}
```

Add the `FakeAppRunner` to `tests/Naudit.Tests/Fakes/` (implements `IAppRunner`; `RunAsync` records `RunCalled`, returns a `RunningApp("http://naudit-dast-app-x:8080/", "naudit-dast-net-x", "naudit-dast-app-x", "naudit-dast-pw-x", () => { Disposed = true; return ValueTask.CompletedTask; })` unless configured to return null). Confirm `FakeChatClient` can be constructed with a fixed response string returning it from `GetResponseAsync` — if the existing fake differs, adapt to its constructor.

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests`
Expected: FAIL — `DastAnalyzer` does not exist (CS0246).

- [ ] **Step 4: Write the analyzer**

Create `src/Naudit.Infrastructure/Dast/DastAnalyzer.cs`. It wraps the global `IChatClient` in a locally-bounded `UseFunctionInvocation` (cap `MaxProbeSteps`), passes the probe tools in `ChatOptions.Tools`, and parses the JSON. The `probeToolsOverride` seam lets tests bypass the live MCP; production passes `null` and the analyzer opens a real `DastProbeSession`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Docker;

namespace Naudit.Infrastructure.Dast;

/// <summary>Dynamische Prüfung als weiterer ISastAnalyzer: baut/startet die PR-App (PR-1-Runner),
/// treibt den Playwright-MCP-Server durch einen begrenzten agentischen Loop und mappt die
/// JSON-Beobachtungen des Modells auf ScanFinding(Category=Dast). Reines Grounding, Verdict bleibt am
/// Gate. Fail-open über alles; garantierter Teardown der DAST-Topologie über RunningApp.</summary>
public sealed class DastAnalyzer(
    IAppRunner runner,
    DastOptions options,
    IChatClient chatClient,
    IDockerClient docker,
    ILoggerFactory loggerFactory,
    IReadOnlyList<AITool>? probeToolsOverride = null) : ISastAnalyzer
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<DastAnalyzer>();

    public string Name => "dast";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        if (!options.AppliesTo(workspace.ProjectId))
            return [];

        try
        {
            await using var app = await runner.RunAsync(workspace, ct);
            if (app is null) return [];   // nicht anwendbar / kam nicht hoch — Runner hat schon geloggt

            DastProbeSession? session = null;
            try
            {
                IReadOnlyList<AITool> tools;
                if (probeToolsOverride is not null)
                {
                    tools = probeToolsOverride;   // Testnaht: kein echter MCP-Server
                }
                else
                {
                    session = await DastProbeSession.StartAsync(docker, options, app.ProbeContainerName, loggerFactory, ct);
                    tools = session.Tools;
                }

                var raw = await RunProbeLoopAsync(app.InternalUrl, tools, ct);
                return ParseFindings(raw);
            }
            finally
            {
                if (session is not null) await session.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // echter Aufrufer-Abbruch propagiert
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DAST-Probing abgebrochen — Review läuft ohne dynamische Funde weiter.");
            return [];
        }
    }

    private async Task<string> RunProbeLoopAsync(string appUrl, IReadOnlyList<AITool> tools, CancellationToken ct)
    {
        var client = tools.Count > 0
            ? chatClient.AsBuilder().UseFunctionInvocation(loggerFactory,
                c => c.MaximumIterationsPerRequest = Math.Max(1, options.MaxProbeSteps)).Build()
            : chatClient;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, DastProbePrompt.System(appUrl, options.MaxProbeSteps)),
            new(ChatRole.User, $"Probe the app at {appUrl} now and return the findings JSON."),
        };
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        if (tools.Count > 0) chatOptions.Tools = [.. tools];
        var response = await client.GetResponseAsync(messages, chatOptions, ct);
        return response.Text;
    }

    /// <summary>Non-JSON / Schema-Fehler ⇒ leere Liste (Grounding-Schritt, nicht fail-closed).</summary>
    private IReadOnlyList<ScanFinding> ParseFindings(string text)
    {
        try
        {
            var doc = JsonSerializer.Deserialize<ProbeResult>(text, JsonOpts);
            if (doc?.Findings is not { Count: > 0 }) return [];
            return doc.Findings
                .Where(f => f is not null)
                .Select(f => new ScanFinding("dast", FindingCategory.Dast, MapSeverity(f!.Severity),
                    $"{f.Summary} ({f.Endpoint})"))
                .ToList();
        }
        catch (JsonException)
        {
            _logger.LogInformation("DAST: Probing-Antwort war kein gültiges JSON — keine dynamischen Funde.");
            return [];
        }
    }

    private static FindingSeverity MapSeverity(string? s) => s?.ToLowerInvariant() switch
    {
        "high" => FindingSeverity.High,
        "medium" => FindingSeverity.Medium,
        _ => FindingSeverity.Low,
    };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private sealed record ProbeResult(List<ProbeFinding?>? Findings);
    private sealed record ProbeFinding(string? Severity, string? Endpoint, string? Summary);
}
```

> **Implementer note on `FindingSeverity`:** use the enum values that already exist in `src/Naudit.Core/Models/` (the SAST findings use it). If the members are named differently (e.g. `Info`/`Critical`), map accordingly and keep three tiers.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DastAnalyzerTests`
Expected: PASS (2).

- [ ] **Step 6: Full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS — 706.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Infrastructure/Dast/DastProbePrompt.cs src/Naudit.Infrastructure/Dast/DastAnalyzer.cs tests/Naudit.Tests
git commit -m "feat(dast): DastAnalyzer — Runner + Probing-Loop + JSON→ScanFinding(Dast)"
```

---

