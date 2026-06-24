# SAST/SCA-Grounding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit klont den MR/PR-Head selbst, lässt pluggbare SAST/SCA-Analyzer (Semgrep, Trivy, dotnet-sca) laufen und speist deren normalisierte Funde als Grounding in den bestehenden LLM-Review.

**Architecture:** Drei neue Core-Abstraktionen (`IWorkspaceProvider`, `ISastAnalyzer`, `IFindingReducer`) + ein Core-Model (`ScanFinding`), implementiert in `Naudit.Infrastructure/Sast` über die wiederverwendete `IProcessRunner`-Prozessnaht. `ReviewService` orchestriert Checkout → parallele Analyse → Annotation `[in diff]`/`[pre-existing]` → deterministische Verdichtung → Grounding-Sektion im Prompt. Das LLM trifft weiterhin **allein** das Verdict (Grounding-only).

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, xUnit, Semgrep, Trivy, dotnet CLI; Tools über `System.Diagnostics.Process` hinter `IProcessRunner`.

## Global Constraints

- **Core-Regel:** `Naudit.Core` referenziert ausschließlich `Microsoft.Extensions.AI.Abstractions`. **Keine** Tool-/Provider-/Platform-SDKs **und keine** Logging-Abstraktionen in Core. Logging nur in Infrastructure.
- **Solution-Datei ist `Naudit.slnx`** (nicht `.sln`). Build: `dotnet build Naudit.slnx`. Tests: `dotnet test Naudit.slnx`.
- **TDD:** red → green, **ein Commit pro Task**. Reine Daten-/Interface-Typen ohne Verhalten werden per Build verifiziert (kein performativer Test).
- **Code-Kommentare auf Deutsch**; `README`/`docs/` auf Englisch.
- **.NET 10:** kein `public partial class Program {}` (Program ist bereits public).
- **Grounding-only:** Tools setzen **nie** das Verdict; das LLM bleibt die einzige Verdict-Instanz.
- **Funde repo-weit**, annotiert `[in diff]`/`[pre-existing]`; nichts wird gefiltert außer deterministischem Dedup/Cap.
- **Rückwärtskompatibel:** `Naudit:Sast:Enabled=false` (oder leere Analyzer-Liste) ⇒ **exakt heutiges diff-only-Verhalten** (kein Checkout).

## Prerequisite / Branch

Diese Arbeit setzt die `IProcessRunner`-Prozessnaht voraus, die heute nur auf `feat/claudecode-provider` existiert. **Dieser Plan wird auf einem Branch ausgeführt, der `feat/claudecode-provider` enthält** — entweder weil dieser bereits nach `main` gemergt wurde (dann rebaset `feat/sast-grounding` auf `main`), oder indem `feat/sast-grounding` auf `feat/claudecode-provider` aufsetzt. Vor Task 1 sicherstellen: `src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs` existiert und `dotnet test Naudit.slnx` ist grün.

## File Structure

**Neu (Core):**
- `src/Naudit.Core/Models/ScanFinding.cs` — `FindingCategory`, `FindingSeverity`, `ScanFinding`.
- `src/Naudit.Core/Abstractions/IWorkspaceProvider.cs` — `IWorkspaceProvider`, `IReviewWorkspace`.
- `src/Naudit.Core/Abstractions/ISastAnalyzer.cs` — `ISastAnalyzer`.
- `src/Naudit.Core/Abstractions/IFindingReducer.cs` — `IFindingReducer`.

**Neu (Infrastructure):**
- `src/Naudit.Infrastructure/Process/IProcessRunner.cs` *(verschoben aus `Ai/ClaudeCode/`)*.
- `src/Naudit.Infrastructure/Process/SystemProcessRunner.cs` *(verschoben)*.
- `src/Naudit.Infrastructure/Sast/SastOptions.cs`
- `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs`
- `src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs`
- `src/Naudit.Infrastructure/Sast/SemgrepAnalyzer.cs`
- `src/Naudit.Infrastructure/Sast/TrivyAnalyzer.cs`
- `src/Naudit.Infrastructure/Sast/DotnetScaAnalyzer.cs`

**Geändert:**
- `src/Naudit.Core/Abstractions/IGitPlatform.cs` — `GetCheckoutAsync` + `RepoCheckoutInfo`.
- `src/Naudit.Core/Review/PromtBuilder.cs` — `Build`-Signatur + Grounding-Sektion + System-Prompt.
- `src/Naudit.Core/Review/ReviewService.cs` — Orchestrierung.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` + `GitLabDtos.cs`.
- `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` + `GitHubDtos.cs`.
- `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs` + `Ai/AiClientFactory.cs` (using nach Move).
- `src/Naudit.Infrastructure/DependencyInjection.cs` — SAST-Wiring.
- `Dockerfile` — Semgrep + Trivy im Runtime-Image.
- `docs/`, `CLAUDE.md`, `appsettings.json`.

**Neu (Tests):**
- `tests/Naudit.Tests/Fakes/FakeWorkspaceProvider.cs`, `FakeSastAnalyzer.cs`, `FakeFindingReducer.cs`.
- `tests/Naudit.Tests/DeterministicFindingReducerTests.cs`, `GitWorkspaceProviderTests.cs`,
  `SemgrepAnalyzerTests.cs`, `TrivyAnalyzerTests.cs`, `DotnetScaAnalyzerTests.cs`,
  `SastWiringTests.cs`.
- Geändert: `ReviewServiceTests.cs`, `GitLabPlatformTests.cs`, `GitHubPlatformTests.cs`, `Fakes/FakeGitPlatform.cs`, `Fakes/StubProcessRunner.cs`.

---

### Task 1: `IProcessRunner` nach `Infrastructure/Process` verschieben

Reiner Namespace-Refactor, damit die Prozessnaht nicht mehr AI-spezifisch verortet ist. Alle bestehenden Tests bleiben grün.

**Files:**
- Move: `src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs` → `src/Naudit.Infrastructure/Process/IProcessRunner.cs`
- Move: `src/Naudit.Infrastructure/Ai/ClaudeCode/SystemProcessRunner.cs` → `src/Naudit.Infrastructure/Process/SystemProcessRunner.cs`
- Modify: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`, `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`, `tests/Naudit.Tests/Fakes/StubProcessRunner.cs`, `tests/Naudit.Tests/SystemProcessRunnerTests.cs`, `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`

**Interfaces:**
- Produces: `namespace Naudit.Infrastructure.Process` mit `IProcessRunner`, `ProcessSpec`, `ProcessResult`, `SystemProcessRunner` (Signaturen unverändert).

- [ ] **Step 1: Dateien verschieben**

```bash
git mv src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs src/Naudit.Infrastructure/Process/IProcessRunner.cs
git mv src/Naudit.Infrastructure/Ai/ClaudeCode/SystemProcessRunner.cs src/Naudit.Infrastructure/Process/SystemProcessRunner.cs
```

- [ ] **Step 2: Namespace in beiden verschobenen Dateien ändern**

In `Process/IProcessRunner.cs` und `Process/SystemProcessRunner.cs` die Zeile
`namespace Naudit.Infrastructure.Ai.ClaudeCode;` ersetzen durch:

```csharp
namespace Naudit.Infrastructure.Process;
```

- [ ] **Step 3: `using` in den Verbrauchern ergänzen**

In `Ai/ClaudeCode/ClaudeCodeChatClient.cs` und `Ai/AiClientFactory.cs` jeweils oben ergänzen:

```csharp
using Naudit.Infrastructure.Process;
```

In `tests/Naudit.Tests/Fakes/StubProcessRunner.cs`, `tests/Naudit.Tests/SystemProcessRunnerTests.cs` und `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs` die Zeile `using Naudit.Infrastructure.Ai.ClaudeCode;` ersetzen durch `using Naudit.Infrastructure.Process;` (in `ClaudeCodeChatClientTests.cs` zusätzlich `using Naudit.Infrastructure.Ai.ClaudeCode;` **behalten**, falls dort der `ClaudeCodeChatClient`-Typ referenziert wird).

- [ ] **Step 4: Build prüfen, Rest der Fehler über das Compiler-Feedback fixen**

Run: `dotnet build Naudit.slnx`
Expected: PASS. Falls noch `CS0246`/`CS0234` zu `IProcessRunner`/`ProcessSpec`/`SystemProcessRunner` auftreten, in der genannten Datei `using Naudit.Infrastructure.Process;` ergänzen, bis grün.

- [ ] **Step 5: Volle Testsuite grün**

Run: `dotnet test Naudit.slnx`
Expected: PASS (alle bestehenden Tests, insb. `ClaudeCodeChatClientTests`, `SystemProcessRunnerTests`, `AiClientFactoryTests`).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(infra): IProcessRunner-Naht nach Infrastructure/Process verschieben"
```

---

### Task 2: Core-Verträge — `ScanFinding` + drei Abstraktionen

Reine Verträge ohne Verhalten ⇒ Build-Verifikation statt Test.

**Files:**
- Create: `src/Naudit.Core/Models/ScanFinding.cs`
- Create: `src/Naudit.Core/Abstractions/IWorkspaceProvider.cs`
- Create: `src/Naudit.Core/Abstractions/ISastAnalyzer.cs`
- Create: `src/Naudit.Core/Abstractions/IFindingReducer.cs`

**Interfaces:**
- Produces: `ScanFinding(string Tool, FindingCategory Category, FindingSeverity Severity, string Message, string? RuleId=null, string? FilePath=null, int? Line=null)` mit `bool InDiff { get; init; }`; `enum FindingCategory { Sast, Sca }`; `enum FindingSeverity { Info, Low, Medium, High, Critical }`; `IWorkspaceProvider.CheckoutAsync`, `IReviewWorkspace.RootPath`, `ISastAnalyzer.AnalyzeAsync`/`Name`, `IFindingReducer.ReduceAsync`.

- [ ] **Step 1: `ScanFinding.cs` anlegen**

```csharp
namespace Naudit.Core.Models;

/// <summary>Art des Funds: statische Code-Analyse vs. Dependency-/SCA-Scan.</summary>
public enum FindingCategory { Sast, Sca }

/// <summary>Normalisierter Schweregrad über alle Tools hinweg (Reihenfolge = Rang für Sortierung).</summary>
public enum FindingSeverity { Info, Low, Medium, High, Critical }

/// <summary>Ein tool-agnostischer, normalisierter Fund (Semgrep/Trivy/dotnet/…).</summary>
public sealed record ScanFinding(
    string Tool,
    FindingCategory Category,
    FindingSeverity Severity,
    string Message,
    string? RuleId = null,
    string? FilePath = null,
    int? Line = null)
{
    /// <summary>Vom Orchestrator gesetzt: liegt der Fund in einer im MR geänderten Datei?</summary>
    public bool InDiff { get; init; }
}
```

- [ ] **Step 2: `IWorkspaceProvider.cs` anlegen**

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Materialisiert den Quellcode eines ReviewRequests in ein lokales, wegwerfbares Verzeichnis.</summary>
public interface IWorkspaceProvider
{
    Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Handle auf den ausgecheckten Quellbaum; DisposeAsync räumt das Temp-Verzeichnis auf.</summary>
public interface IReviewWorkspace : IAsyncDisposable
{
    string RootPath { get; }
}
```

- [ ] **Step 3: `ISastAnalyzer.cs` anlegen**

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Pluggbarer Code-Scanner (SAST/SCA). Mehrere Implementierungen registrierbar.
/// Nicht anwendbar (kein passendes Projekt etc.) ⇒ leere Liste, kein Fehler.</summary>
public interface ISastAnalyzer
{
    string Name { get; }
    Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

- [ ] **Step 4: `IFindingReducer.cs` anlegen**

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Verdichtet/normalisiert die aggregierten Funde vor dem Grounding. Default deterministisch;
/// optional später LLM-basiert. Liefert wieder ScanFinding[], damit der Prompt-Aufbau reducer-agnostisch bleibt.</summary>
public interface IFindingReducer
{
    Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

- [ ] **Step 5: Build prüfen**

Run: `dotnet build Naudit.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core
git commit -m "feat(core): ScanFinding-Model + IWorkspaceProvider/ISastAnalyzer/IFindingReducer"
```

---

### Task 3: `DeterministicFindingReducer`

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs`
- Test: `tests/Naudit.Tests/DeterministicFindingReducerTests.cs`

**Interfaces:**
- Consumes: `IFindingReducer`, `ScanFinding`, `FindingCategory`, `FindingSeverity`.
- Produces: `DeterministicFindingReducer(int maxFindingsPerGroup = 20) : IFindingReducer`.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using Naudit.Core.Models;
using Naudit.Infrastructure.Sast;
using Xunit;

namespace Naudit.Tests;

public class DeterministicFindingReducerTests
{
    private static ScanFinding Sast(string file, int line, string rule, FindingSeverity sev, bool inDiff = false)
        => new("semgrep", FindingCategory.Sast, sev, "msg", rule, file, line) { InDiff = inDiff };

    [Fact]
    public async Task Reduce_dedupesIdenticalLocationRuleCategory()
    {
        var reducer = new DeterministicFindingReducer();
        var input = new[] { Sast("a.cs", 1, "R1", FindingSeverity.High), Sast("a.cs", 1, "R1", FindingSeverity.High) };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Single(result);
    }

    [Fact]
    public async Task Reduce_sortsBySeverityDesc_thenInDiffFirst()
    {
        var reducer = new DeterministicFindingReducer();
        var low = Sast("a.cs", 1, "R1", FindingSeverity.Low);
        var critNotInDiff = Sast("b.cs", 2, "R2", FindingSeverity.Critical, inDiff: false);
        var critInDiff = Sast("c.cs", 3, "R3", FindingSeverity.Critical, inDiff: true);

        var result = await reducer.ReduceAsync(new[] { low, critNotInDiff, critInDiff }, []);

        Assert.Equal("R3", result[0].RuleId); // Critical + InDiff zuerst
        Assert.Equal("R2", result[1].RuleId); // Critical, nicht InDiff
        Assert.Equal("R1", result[2].RuleId); // Low zuletzt
    }

    [Fact]
    public async Task Reduce_capsPerCategory()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 2);
        var input = new[]
        {
            Sast("a.cs", 1, "R1", FindingSeverity.Critical),
            Sast("b.cs", 2, "R2", FindingSeverity.High),
            Sast("c.cs", 3, "R3", FindingSeverity.Low),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, f => f.RuleId == "R3"); // niedrigste Severity fällt raus
    }
}
```

- [ ] **Step 2: Test schlägt fehl (kompiliert nicht — Typ fehlt)**

Run: `dotnet test Naudit.slnx --filter DeterministicFindingReducerTests`
Expected: FAIL/Build-Fehler "DeterministicFindingReducer not found".

- [ ] **Step 3: Implementierung schreiben**

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Sast;

/// <summary>Deterministische Verdichtung: Dedup nach (Datei, Zeile, RuleId, Category),
/// Sortierung Severity↓ dann InDiff zuerst, Cap pro Category. Reproduzierbar, kein Recall-Risiko.</summary>
public sealed class DeterministicFindingReducer(int maxFindingsPerGroup = 20) : IFindingReducer
{
    public Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var reduced = findings
            .GroupBy(f => (f.FilePath, f.Line, f.RuleId, f.Category))
            .Select(g => g.First())                       // Dedup beweisbarer Duplikate
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.InDiff)
            .GroupBy(f => f.Category)
            .SelectMany(g => g.Take(maxFindingsPerGroup)) // Cap pro Category
            .ToList();

        return Task.FromResult<IReadOnlyList<ScanFinding>>(reduced);
    }
}
```

- [ ] **Step 4: Test grün**

Run: `dotnet test Naudit.slnx --filter DeterministicFindingReducerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs tests/Naudit.Tests/DeterministicFindingReducerTests.cs
git commit -m "feat(sast): deterministischer FindingReducer (dedup/sort/cap)"
```

---

### Task 4: `PromptBuilder` — Grounding-Sektion + System-Prompt

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Consumes: `ScanFinding`, `FindingCategory`, `FindingSeverity`.
- Produces: `PromptBuilder.Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes, IReadOnlyList<ScanFinding>? findings = null)` (optionaler Parameter ⇒ bestehende Aufrufer kompilieren weiter). `DefaultSystemPrompt` enthält Grounding- + Toolchain-Sätze.

- [ ] **Step 1: Failing Tests ergänzen** (an `PromtBuilderTests.cs` anhängen)

```csharp
    [Fact]
    public void Build_rendersFindings_withScopeLabels()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ +1 @@") };
        var findings = new[]
        {
            new ScanFinding("semgrep", FindingCategory.Sast, FindingSeverity.High, "sqli", "rule.sqli", "src/Foo.cs", 42) { InDiff = true },
            new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Critical, "Newtonsoft.Json 9.0.1", "CVE-2024-1", "packages.lock.json") { InDiff = false },
        };

        var text = PromptBuilder.Build("SYS", request, changes, findings)[1].Text!;

        Assert.Contains("## SAST", text);
        Assert.Contains("[HIGH][in diff] semgrep", text);
        Assert.Contains("src/Foo.cs:42", text);
        Assert.Contains("## Dependency / SCA", text);
        Assert.Contains("[CRITICAL][pre-existing] trivy", text);
        Assert.DoesNotContain("No tool findings.", text);
    }

    [Fact]
    public void Build_withoutFindings_saysNoToolFindings()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes)[1].Text!;

        Assert.Contains("No tool findings.", text);
    }

    [Fact]
    public void DefaultSystemPrompt_groundsToolchain()
    {
        Assert.Contains("do NOT flag", PromptBuilder.DefaultSystemPrompt);
        Assert.Contains("grounding", PromptBuilder.DefaultSystemPrompt);
    }
```

Ergänze oben in der Testdatei `using Naudit.Core.Models;` (falls noch nicht vorhanden — wird für `ScanFinding` gebraucht).

- [ ] **Step 2: Tests schlagen fehl**

Run: `dotnet test Naudit.slnx --filter PromptBuilderTests`
Expected: FAIL (Build-Fehler: `Build` hat keinen 4. Parameter / Strings fehlen).

- [ ] **Step 3: `DefaultSystemPrompt` erweitern**

In `PromtBuilder.cs` `DefaultSystemPrompt` ersetzen durch:

```csharp
    public const string DefaultSystemPrompt =
        "You are Naudit, a senior code reviewer. Review the merge request diff below. " +
        "Focus on correctness bugs, security issues and clear maintainability problems. Be concise. " +
        "Static-analysis and dependency-scan results may be provided below as grounding; treat them as reliable signals. " +
        "Assume the project's target framework and toolchain are valid and current; do NOT flag a framework or SDK version as nonexistent or unsupported. " +
        "Respond ONLY with a JSON object with exactly two fields: " +
        "\"summary\" - GitHub-flavored Markdown (a one-line summary followed by a bullet list of findings; " +
        "if there are no significant issues, say so briefly) - and " +
        "\"verdict\" - either \"approve\" or \"request_changes\" " +
        "(use \"request_changes\" only when there are correctness or security bugs that should block the merge).";
```

- [ ] **Step 4: `Build` erweitern + Grounding-Helfer**

`Build`-Signatur und Rumpf in `PromtBuilder.cs` ersetzen durch:

```csharp
    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null)
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

        AppendFindings(sb, findings ?? []);

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }

    // Grounding-Sektion: alle Funde repo-weit, annotiert [in diff]/[pre-existing], gruppiert nach Category.
    private static void AppendFindings(StringBuilder sb, IReadOnlyList<ScanFinding> findings)
    {
        sb.AppendLine();
        sb.AppendLine("# Static-analysis & dependency findings (grounding — tools run on the repo, treat as reliable)");
        if (findings.Count == 0)
        {
            sb.AppendLine("No tool findings.");
            return;
        }
        sb.AppendLine("Prioritize [in diff] (introduced/touched by this MR). [pre-existing] were already in the repo.");

        AppendCategory(sb, "Dependency / SCA", findings.Where(f => f.Category == FindingCategory.Sca));
        AppendCategory(sb, "SAST", findings.Where(f => f.Category == FindingCategory.Sast));
    }

    private static void AppendCategory(StringBuilder sb, string heading, IEnumerable<ScanFinding> items)
    {
        var list = items.ToList();
        if (list.Count == 0)
            return;
        sb.AppendLine();
        sb.AppendLine($"## {heading}");
        foreach (var f in list)
        {
            var scope = f.InDiff ? "in diff" : "pre-existing";
            var loc = f.FilePath is null ? "" : f.Line is int ln ? $" · {f.FilePath}:{ln}" : $" · {f.FilePath}";
            var rule = f.RuleId is null ? "" : $" · {f.RuleId}";
            sb.AppendLine($"- [{f.Severity.ToString().ToUpperInvariant()}][{scope}] {f.Tool}{rule}{loc} → {f.Message}");
        }
    }
```

Oben in `PromtBuilder.cs` sicherstellen: `using Naudit.Core.Models;` ist vorhanden (für `ScanFinding`/`FindingCategory`).

- [ ] **Step 5: Tests grün (inkl. bestehender)**

Run: `dotnet test Naudit.slnx --filter PromptBuilderTests`
Expected: PASS (auch der bestehende `Build_putsSystemPromptFirst_andEmbedsDiffsAndPaths` — der nutzt den Default-Parameter).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(core): Grounding-Sektion im Prompt + Toolchain-Grounding im System-Prompt"
```

---

### Task 5: `ReviewService`-Orchestrierung + Test-Fakes

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeWorkspaceProvider.cs`, `FakeSastAnalyzer.cs`, `FakeFindingReducer.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `IWorkspaceProvider`, `IReviewWorkspace`, `ISastAnalyzer`, `IFindingReducer`, `ScanFinding`, `PromptBuilder.Build(..., findings)`.
- Produces: `ReviewService(IChatClient, IGitPlatform, ReviewOptions, IWorkspaceProvider, IEnumerable<ISastAnalyzer>, IFindingReducer)`; Verhalten unverändert bzgl. Verdict-Mapping/PostSummary.

- [ ] **Step 1: Fakes anlegen**

`tests/Naudit.Tests/Fakes/FakeWorkspaceProvider.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeWorkspaceProvider(string rootPath = "/tmp/ws") : IWorkspaceProvider
{
    public bool CheckoutCalled { get; private set; }
    public bool ThrowOnCheckout { get; set; }

    public Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        CheckoutCalled = true;
        if (ThrowOnCheckout)
            throw new InvalidOperationException("checkout failed");
        return Task.FromResult<IReviewWorkspace>(new FakeWorkspace(rootPath));
    }

    private sealed class FakeWorkspace(string root) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

`tests/Naudit.Tests/Fakes/FakeSastAnalyzer.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeSastAnalyzer(string name, IReadOnlyList<ScanFinding> findings, bool throws = false) : ISastAnalyzer
{
    public string Name => name;

    public Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        if (throws)
            throw new InvalidOperationException("analyzer boom");
        return Task.FromResult(findings);
    }
}
```

`tests/Naudit.Tests/Fakes/FakeFindingReducer.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Identitäts-Reducer: isoliert die ReviewService-Tests von der Verdichtungslogik.
internal sealed class FakeFindingReducer : IFindingReducer
{
    public Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult(findings);
}
```

- [ ] **Step 2: Bestehende `ReviewServiceTests` auf Helfer umstellen + neue Tests**

In `ReviewServiceTests.cs` oben ergänzen: `using Naudit.Core.Abstractions;`. Einen Helfer einfügen und die vier bestehenden `new ReviewService(chat, git, ...)`-Aufrufe durch `CreateService(...)` ersetzen:

```csharp
    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null)
        => new(chat, git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer());
```

Beispiel-Ersetzung (alle vier analog): aus
`var service = new ReviewService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });`
wird
`var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" });`

Neue Tests anhängen:

```csharp
    [Fact]
    public async Task ReviewAsync_groundsFindings_inPrompt_andAnnotatesInDiff()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var finding = new ScanFinding("semgrep", FindingCategory.Sast, FindingSeverity.High, "sqli", "rule.sqli", "a.cs", 5);
        var analyzers = new[] { new FakeSastAnalyzer("semgrep", new[] { finding }) };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.Contains("[HIGH][in diff] semgrep", userText);
        Assert.Contains("a.cs:5", userText);
    }

    [Fact]
    public async Task ReviewAsync_marksFindingPreExisting_whenFileNotInDiff()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var finding = new ScanFinding("semgrep", FindingCategory.Sast, FindingSeverity.Low, "x", "r", "other.cs", 1);
        var analyzers = new[] { new FakeSastAnalyzer("semgrep", new[] { finding }) };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        await service.ReviewAsync(Request);

        Assert.Contains("[LOW][pre-existing] semgrep", chat.LastMessages![1].Text!);
    }

    [Fact]
    public async Task ReviewAsync_oneAnalyzerThrows_stillReviewsWithOthers()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var good = new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Critical, "CVE", "CVE-1", "lock");
        var analyzers = new ISastAnalyzer[]
        {
            new FakeSastAnalyzer("boom", Array.Empty<ScanFinding>(), throws: true),
            new FakeSastAnalyzer("trivy", new[] { good }),
        };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Contains("CVE-1", chat.LastMessages![1].Text!);
    }

    [Fact]
    public async Task ReviewAsync_checkoutFails_degradesToDiffOnly()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var analyzers = new[] { new FakeSastAnalyzer("semgrep", Array.Empty<ScanFinding>()) };
        var ws = new FakeWorkspaceProvider { ThrowOnCheckout = true };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers, ws);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Contains("No tool findings.", chat.LastMessages![1].Text!);
        Assert.Equal(1, git.PostCallCount);
    }

    [Fact]
    public async Task ReviewAsync_noAnalyzers_doesNotCheckout()
    {
        var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ws = new FakeWorkspaceProvider();
        var service = CreateService(chat, git, new ReviewOptions(), analyzers: null, workspace: ws);

        await service.ReviewAsync(Request);

        Assert.False(ws.CheckoutCalled);
    }
```

Ergänze in `ReviewServiceTests.cs` die `using`-Zeilen `using Naudit.Core.Models;` (für `ScanFinding`/`FindingCategory`/`FindingSeverity`, falls nicht vorhanden) und `using Naudit.Tests.Fakes;`.

- [ ] **Step 3: Tests schlagen fehl**

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests`
Expected: FAIL (ctor-Arität / Checkout-Logik fehlt).

- [ ] **Step 4: `ReviewService` umbauen**

Kompletten Inhalt von `ReviewService.cs` ersetzen durch:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewService(
    IChatClient chatClient,
    IGitPlatform gitPlatform,
    ReviewOptions options,
    IWorkspaceProvider workspaceProvider,
    IEnumerable<ISastAnalyzer> analyzers,
    IFindingReducer findingReducer)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private readonly IReadOnlyList<ISastAnalyzer> _analyzers = analyzers.ToList();

    public async Task<ReviewResult> ReviewAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        var findings = await CollectFindingsAsync(request, changes, ct);

        var messages = PromptBuilder.Build(options.SystemPrompt, request, changes, findings);
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        var response = await chatClient.GetResponseAsync(messages, chatOptions, ct);

        var parsed = JsonSerializer.Deserialize<LlmReviewResponse>(response.Text, JsonOpts)
            ?? throw new InvalidOperationException("LLM lieferte keine parsebare Review-Antwort.");

        // Fail-closed: nur explizite Verdicts akzeptieren; alles andere ist ein Fehler.
        var verdict = parsed.Verdict?.ToLowerInvariant() switch
        {
            "request_changes" => ReviewVerdict.RequestChanges,
            "approve" => ReviewVerdict.Approve,
            _ => throw new InvalidOperationException($"Unerwartetes Verdict vom LLM: '{parsed.Verdict}'."),
        };

        await gitPlatform.PostSummaryAsync(request, parsed.Summary, ct);
        return new ReviewResult(parsed.Summary, verdict);
    }

    // SAST/SCA-Grounding. Ohne Analyzer (Feature aus) sofort leer → exakt diff-only wie früher.
    // Checkout-Fehler degradiert auf diff-only; ein einzelner Analyzer-Fehler kippt den Review nicht.
    private async Task<IReadOnlyList<ScanFinding>> CollectFindingsAsync(
        ReviewRequest request, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        if (_analyzers.Count == 0)
            return [];

        IReviewWorkspace workspace;
        try
        {
            workspace = await workspaceProvider.CheckoutAsync(request, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return []; // Checkout fehlgeschlagen → diff-only (Infrastructure hat geloggt)
        }

        await using (workspace)
        {
            var results = await Task.WhenAll(_analyzers.Select(a => SafeAnalyzeAsync(a, workspace, changes, ct)));

            var changed = new HashSet<string>(changes.Select(c => c.FilePath));
            var annotated = results
                .SelectMany(r => r)
                .Select(f => f.FilePath is not null && changed.Contains(f.FilePath) ? f with { InDiff = true } : f)
                .ToList();

            return await findingReducer.ReduceAsync(annotated, changes, ct);
        }
    }

    private static async Task<IReadOnlyList<ScanFinding>> SafeAnalyzeAsync(
        ISastAnalyzer analyzer, IReviewWorkspace ws, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        try { return await analyzer.AnalyzeAsync(ws, changes, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return []; }
    }

    private sealed record LlmReviewResponse(string Summary, string Verdict);
}
```

- [ ] **Step 5: Tests grün**

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests`
Expected: PASS (alte + neue).

- [ ] **Step 6: Volle Suite grün**

Run: `dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/
git commit -m "feat(core): ReviewService orchestriert Checkout+Scan+Grounding (diff-only fallback)"
```

---

### Task 6: `IGitPlatform.GetCheckoutAsync` + Plattform-Implementierungen

**Files:**
- Modify: `src/Naudit.Core/Abstractions/IGitPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`, `Git/GitLab/GitLabDtos.cs`, `Git/GitLab/GitLabOptions.cs` (kein Change nötig), `Git/GitHub/GitHubPlatform.cs`, `Git/GitHub/GitHubDtos.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`, `GitLabPlatformTests.cs`, `GitHubPlatformTests.cs`

**Interfaces:**
- Produces: `IGitPlatform.GetCheckoutAsync(ReviewRequest, CancellationToken) → RepoCheckoutInfo`; `record RepoCheckoutInfo(string CloneUrl, string HeadRef)`.

- [ ] **Step 1: Failing Tests (GitLab) ergänzen**

In `GitLabPlatformTests.cs`: oben `using Microsoft.Extensions.Options;` und `using Naudit.Infrastructure.Git.GitLab;` (vorhanden) ergänzen. Die beiden bestehenden `new GitLabPlatform(<client>)`-Aufrufe ersetzen durch `new GitLabPlatform(<client>, Options.Create(new GitLabOptions { Token = "tok" }))`. Neuen Test anhängen:

```csharp
    [Fact]
    public async Task GetCheckoutAsync_buildsCloneUrlWithToken_andMrRef()
    {
        const string json = """{ "http_url_to_repo": "https://gitlab.example.com/group/proj.git" }""";
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.OK, json),
            Options.Create(new GitLabOptions { Token = "tok" }));

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://oauth2:tok@gitlab.example.com/group/proj.git", info.CloneUrl);
        Assert.Equal("refs/merge-requests/42/head", info.HeadRef);
    }
```

- [ ] **Step 2: Failing Tests (GitHub) ergänzen**

In `GitHubPlatformTests.cs`: `using Microsoft.Extensions.Options;` ergänzen, bestehende `new GitHubPlatform(<client>)`-Aufrufe um `, Options.Create(new GitHubOptions { Token = "tok" })` erweitern. Neuen Test anhängen (passe `ClientReturning`-Helfernamen an den in dieser Datei vorhandenen an — er liefert einen `HttpClient` mit `BaseAddress`):

```csharp
    [Fact]
    public async Task GetCheckoutAsync_buildsCloneUrlWithToken_andPrRef()
    {
        const string json = """{ "clone_url": "https://github.com/owner/repo.git" }""";
        var platform = new GitHubPlatform(
            ClientReturning(HttpStatusCode.OK, json),
            Options.Create(new GitHubOptions { Token = "tok" }));

        var info = await platform.GetCheckoutAsync(Request);

        Assert.Equal("https://x-access-token:tok@github.com/owner/repo.git", info.CloneUrl);
        Assert.Equal("refs/pull/42/head", info.HeadRef);
    }
```

- [ ] **Step 3: Tests schlagen fehl**

Run: `dotnet test Naudit.slnx --filter "GitLabPlatformTests|GitHubPlatformTests"`
Expected: FAIL (Methode/ctor-Parameter fehlen).

- [ ] **Step 4: Core-Interface erweitern**

`IGitPlatform.cs` ersetzen durch:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Git-Plattform-Adapter. GitLab und GitHub als Implementierungen vorhanden (per Config gewählt).</summary>
public interface IGitPlatform
{
    Task<IReadOnlyList<CodeChange>> GetChangesAsync(ReviewRequest request, CancellationToken ct = default);
    Task PostSummaryAsync(ReviewRequest request, string markdown, CancellationToken ct = default);

    /// <summary>Liefert Klon-URL (inkl. Auth) und Head-Ref des MR/PR für den lokalen Checkout.</summary>
    Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Checkout-Koordinaten. CloneUrl enthält das Token — NICHT loggen.</summary>
public sealed record RepoCheckoutInfo(string CloneUrl, string HeadRef);
```

- [ ] **Step 5: DTOs erweitern**

In `GitLabDtos.cs` der bestehenden Klasse `GitLabProject` eine Property hinzufügen:

```csharp
    [JsonPropertyName("http_url_to_repo")] public string? HttpUrlToRepo { get; set; }
```

In `GitHubDtos.cs` der bestehenden Klasse `GitHubRepository` eine Property hinzufügen:

```csharp
    [JsonPropertyName("clone_url")] public string? CloneUrl { get; set; }
```

- [ ] **Step 6: GitLabPlatform implementieren**

Signatur ändern und Methode ergänzen. Oben `using Microsoft.Extensions.Options;` ergänzen.

```csharp
public sealed class GitLabPlatform(HttpClient http, IOptions<GitLabOptions> options) : IGitPlatform
{
    // ... GetChangesAsync und PostSummaryAsync unverändert ...

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var project = await http.GetFromJsonAsync<GitLabProject>($"api/v4/projects/{request.ProjectId}", ct)
            ?? throw new InvalidOperationException("GitLab lieferte keine Projekt-Infos.");
        if (string.IsNullOrEmpty(project.HttpUrlToRepo))
            throw new InvalidOperationException("GitLab lieferte keine http_url_to_repo.");

        // Token in die Klon-URL einbetten (oauth2:<token>@host).
        var cloneUrl = project.HttpUrlToRepo.Replace("://", $"://oauth2:{options.Value.Token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/merge-requests/{request.MergeRequestIid}/head");
    }
}
```

- [ ] **Step 7: GitHubPlatform implementieren**

```csharp
public sealed class GitHubPlatform(HttpClient http, IOptions<GitHubOptions> options) : IGitPlatform
{
    // ... GetChangesAsync und PostSummaryAsync unverändert ...

    public async Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var repo = await http.GetFromJsonAsync<GitHubRepository>($"repos/{request.ProjectId}", ct)
            ?? throw new InvalidOperationException("GitHub lieferte keine Repo-Infos.");
        if (string.IsNullOrEmpty(repo.CloneUrl))
            throw new InvalidOperationException("GitHub lieferte keine clone_url.");

        var cloneUrl = repo.CloneUrl.Replace("://", $"://x-access-token:{options.Value.Token}@");
        return new RepoCheckoutInfo(cloneUrl, $"refs/pull/{request.MergeRequestIid}/head");
    }
}
```

Oben in `GitHubPlatform.cs` `using Microsoft.Extensions.Options;` ergänzen.

- [ ] **Step 8: FakeGitPlatform erweitern**

In `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs` ergänzen (Interface erfüllen):

```csharp
    public Task<RepoCheckoutInfo> GetCheckoutAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new RepoCheckoutInfo("https://token@host/repo.git", "refs/test/head"));
```

- [ ] **Step 9: Build + Tests grün**

Run: `dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat(git): GetCheckoutAsync (Klon-URL+Ref) für GitLab und GitHub"
```

---

### Task 7: `GitWorkspaceProvider`

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs`
- Test: `tests/Naudit.Tests/GitWorkspaceProviderTests.cs`
- Modify: `tests/Naudit.Tests/Fakes/StubProcessRunner.cs` (Mehrfach-Aufrufe aufzeichnen)

**Interfaces:**
- Consumes: `IGitPlatform.GetCheckoutAsync`, `IProcessRunner.RunAsync`, `ProcessSpec`, `ProcessResult`.
- Produces: `GitWorkspaceProvider(IGitPlatform, IProcessRunner, ILogger<GitWorkspaceProvider>) : IWorkspaceProvider`.

- [ ] **Step 1: `StubProcessRunner` um Mehrfach-Aufzeichnung erweitern**

In `StubProcessRunner.cs` ergänzen (additiv, bestehende `LastSpec` bleibt):

```csharp
    public List<ProcessSpec> Specs { get; } = new();
```

und in `RunAsync` als erste Zeile nach `LastSpec = spec;`:

```csharp
        Specs.Add(spec);
```

- [ ] **Step 2: Failing Test schreiben**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitWorkspaceProviderTests
{
    private static readonly ReviewRequest Request = new("1", 42, "T");

    [Fact]
    public async Task CheckoutAsync_runsInitFetchCheckout_andExposesRoot()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var git = new FakeGitPlatform([]); // GetCheckoutAsync liefert Dummy-URL/Ref
        var provider = new GitWorkspaceProvider(git, runner, NullLogger<GitWorkspaceProvider>.Instance);

        await using var ws = await provider.CheckoutAsync(Request);

        Assert.True(Directory.Exists(ws.RootPath));
        var gitArgs = runner.Specs.Select(s => string.Join(" ", s.Arguments)).ToList();
        Assert.Contains(gitArgs, a => a.StartsWith("init"));
        Assert.Contains(gitArgs, a => a.Contains("fetch") && a.Contains("refs/test/head"));
        Assert.Contains(gitArgs, a => a.Contains("checkout") && a.Contains("FETCH_HEAD"));
        Assert.All(runner.Specs, s => Assert.Equal("git", s.FileName));
    }

    [Fact]
    public async Task DisposeAsync_deletesWorkspace()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        var ws = await provider.CheckoutAsync(Request);
        var path = ws.RootPath;
        await ws.DisposeAsync();

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task CheckoutAsync_throwsAndCleansUp_whenGitFails()
    {
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("fetch")
                ? new ProcessResult(128, "", "fatal: couldn't fetch")
                : new ProcessResult(0, "", ""));
        var provider = new GitWorkspaceProvider(new FakeGitPlatform([]), runner, NullLogger<GitWorkspaceProvider>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CheckoutAsync(Request));
    }
}
```

- [ ] **Step 3: Test schlägt fehl**

Run: `dotnet test Naudit.slnx --filter GitWorkspaceProviderTests`
Expected: FAIL (Typ fehlt).

- [ ] **Step 4: Implementierung schreiben**

```csharp
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>Klont den MR/PR-Head flach in ein Temp-Verzeichnis (git via IProcessRunner).
/// init → fetch --depth 1 origin &lt;ref&gt; → checkout FETCH_HEAD. Dispose löscht das Verzeichnis.</summary>
public sealed class GitWorkspaceProvider(
    IGitPlatform gitPlatform, IProcessRunner runner, ILogger<GitWorkspaceProvider> logger) : IWorkspaceProvider
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(5);

    public async Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var info = await gitPlatform.GetCheckoutAsync(request, ct);
        var dir = Path.Combine(Path.GetTempPath(), "naudit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            await GitAsync(dir, ct, "init", "-q");
            await GitAsync(dir, ct, "remote", "add", "origin", info.CloneUrl);
            await GitAsync(dir, ct, "fetch", "--depth", "1", "origin", info.HeadRef);
            await GitAsync(dir, ct, "checkout", "-q", "FETCH_HEAD");
            return new GitWorkspace(dir);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    private async Task GitAsync(string dir, CancellationToken ct, params string[] args)
    {
        var spec = new ProcessSpec("git", args, StdIn: null, Environment: null, WorkingDirectory: dir, Timeout: GitTimeout);
        var result = await runner.RunAsync(spec, ct);
        if (result.ExitCode != 0)
        {
            // CloneUrl enthält das Token — nur die Argumente ohne URL loggen.
            logger.LogWarning("git {Op} schlug fehl (Exit {Code}): {Err}", args[0], result.ExitCode, result.StdErr);
            throw new InvalidOperationException($"git {args[0]} schlug fehl (Exit {result.ExitCode}).");
        }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }

    private sealed class GitWorkspace(string root) : IReviewWorkspace
    {
        public string RootPath { get; } = root;

        public ValueTask DisposeAsync()
        {
            TryDelete(RootPath);
            return ValueTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 5: Tests grün**

Run: `dotnet test Naudit.slnx --filter GitWorkspaceProviderTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/GitWorkspaceProvider.cs tests/Naudit.Tests/GitWorkspaceProviderTests.cs tests/Naudit.Tests/Fakes/StubProcessRunner.cs
git commit -m "feat(sast): GitWorkspaceProvider (flacher Klon des MR/PR-Head)"
```

---

### Task 8: `SemgrepAnalyzer`

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/SemgrepAnalyzer.cs`
- Test: `tests/Naudit.Tests/SemgrepAnalyzerTests.cs`

**Interfaces:**
- Consumes: `ISastAnalyzer`, `IReviewWorkspace`, `IProcessRunner`, `ScanFinding`.
- Produces: `SemgrepAnalyzer(IProcessRunner, ILogger<SemgrepAnalyzer>, TimeSpan timeout)`; `Name => "semgrep"`. Läuft `semgrep --config auto --json .` mit `WorkingDirectory = workspace.RootPath`.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class SemgrepAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string Json = """
    { "results": [
        { "check_id": "csharp.lang.security.sqli",
          "path": "src/Foo.cs",
          "start": { "line": 42 },
          "extra": { "message": "SQL injection", "severity": "ERROR" } }
      ], "errors": [] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsSemgrepResult_toSastFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, Json, ""));
        var analyzer = new SemgrepAnalyzer(runner, NullLogger<SemgrepAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("semgrep", f.Tool);
        Assert.Equal(FindingCategory.Sast, f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Equal("src/Foo.cs", f.FilePath);
        Assert.Equal(42, f.Line);
        Assert.Equal("csharp.lang.security.sqli", f.RuleId);
        Assert.Contains("SQL injection", f.Message);
        Assert.Equal("/tmp/x", runner.LastSpec!.WorkingDirectory);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenToolFails()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(2, "", "semgrep crashed"));
        var analyzer = new SemgrepAnalyzer(runner, NullLogger<SemgrepAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        Assert.Empty(findings);
    }
}
```

- [ ] **Step 2: Test schlägt fehl**

Run: `dotnet test Naudit.slnx --filter SemgrepAnalyzerTests`
Expected: FAIL (Typ fehlt).

- [ ] **Step 3: Implementierung schreiben**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SAST über Semgrep: `semgrep --config auto --json .` im Workspace-Root.
/// Führt fremden Code NICHT aus (rein statisch). Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class SemgrepAnalyzer(IProcessRunner runner, ILogger<SemgrepAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "semgrep";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "semgrep", ["--config", "auto", "--json", "."],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "semgrep nicht ausführbar"); return []; }

        // Semgrep liefert Exit 0 (keine Funde) oder 1 (Funde) mit JSON; höhere Codes = echter Fehler.
        if (result.ExitCode > 1 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning("semgrep Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        SemgrepReport? report;
        try { report = JsonSerializer.Deserialize<SemgrepReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "semgrep JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        return report.Results.Select(r => new ScanFinding(
            Tool: "semgrep",
            Category: FindingCategory.Sast,
            Severity: MapSeverity(r.Extra?.Severity),
            Message: r.Extra?.Message ?? r.CheckId ?? "semgrep finding",
            RuleId: r.CheckId,
            FilePath: Normalize(r.Path),
            Line: r.Start?.Line)).ToList();
    }

    private static string? Normalize(string? path)
        => path is null ? null : path.StartsWith("./", StringComparison.Ordinal) ? path[2..] : path;

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "ERROR" => FindingSeverity.High,
        "WARNING" => FindingSeverity.Medium,
        "INFO" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record SemgrepReport([property: JsonPropertyName("results")] List<SemgrepResult>? Results);
    private sealed record SemgrepResult(
        [property: JsonPropertyName("check_id")] string? CheckId,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("start")] SemgrepStart? Start,
        [property: JsonPropertyName("extra")] SemgrepExtra? Extra);
    private sealed record SemgrepStart([property: JsonPropertyName("line")] int Line);
    private sealed record SemgrepExtra(
        [property: JsonPropertyName("message")] string? Message,
        [property: JsonPropertyName("severity")] string? Severity);
}
```

- [ ] **Step 4: Tests grün**

Run: `dotnet test Naudit.slnx --filter SemgrepAnalyzerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/SemgrepAnalyzer.cs tests/Naudit.Tests/SemgrepAnalyzerTests.cs
git commit -m "feat(sast): SemgrepAnalyzer (SAST, JSON-Mapping)"
```

---

### Task 9: `TrivyAnalyzer`

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/TrivyAnalyzer.cs`
- Test: `tests/Naudit.Tests/TrivyAnalyzerTests.cs`

**Interfaces:**
- Produces: `TrivyAnalyzer(IProcessRunner, ILogger<TrivyAnalyzer>, TimeSpan timeout)`; `Name => "trivy"`. Läuft `trivy fs --scanners vuln --format json --quiet .` im Root.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class TrivyAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string Json = """
    { "Results": [
        { "Target": "packages.lock.json",
          "Vulnerabilities": [
            { "VulnerabilityID": "CVE-2024-1234", "PkgName": "Newtonsoft.Json",
              "InstalledVersion": "9.0.1", "Severity": "CRITICAL", "Title": "RCE in parser" } ] } ] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsTrivyVuln_toScaFinding()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, Json, ""));
        var analyzer = new TrivyAnalyzer(runner, NullLogger<TrivyAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("trivy", f.Tool);
        Assert.Equal(FindingCategory.Sca, f.Category);
        Assert.Equal(FindingSeverity.Critical, f.Severity);
        Assert.Equal("CVE-2024-1234", f.RuleId);
        Assert.Equal("packages.lock.json", f.FilePath);
        Assert.Contains("Newtonsoft.Json", f.Message);
        Assert.Contains("9.0.1", f.Message);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenNoResults()
    {
        var runner = new StubProcessRunner(_ => new ProcessResult(0, """{ "Results": [] }""", ""));
        var analyzer = new TrivyAnalyzer(runner, NullLogger<TrivyAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
    }
}
```

- [ ] **Step 2: Test schlägt fehl**

Run: `dotnet test Naudit.slnx --filter TrivyAnalyzerTests`
Expected: FAIL.

- [ ] **Step 3: Implementierung schreiben**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SCA über Trivy: `trivy fs --scanners vuln --format json --quiet .` im Workspace-Root.
/// Führt fremden Code NICHT aus. Fehler ⇒ leere Liste (geloggt).</summary>
public sealed class TrivyAnalyzer(IProcessRunner runner, ILogger<TrivyAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "trivy";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var spec = new ProcessSpec(
            "trivy", ["fs", "--scanners", "vuln", "--format", "json", "--quiet", "."],
            StdIn: null, Environment: null, WorkingDirectory: workspace.RootPath, Timeout: timeout);

        ProcessResult result;
        try { result = await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "trivy nicht ausführbar"); return []; }

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            logger.LogWarning("trivy Exit {Code}: {Err}", result.ExitCode, result.StdErr);
            return [];
        }

        TrivyReport? report;
        try { report = JsonSerializer.Deserialize<TrivyReport>(result.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "trivy JSON nicht parsebar"); return []; }

        if (report?.Results is null)
            return [];

        return report.Results
            .Where(r => r.Vulnerabilities is not null)
            .SelectMany(r => r.Vulnerabilities!.Select(v => new ScanFinding(
                Tool: "trivy",
                Category: FindingCategory.Sca,
                Severity: MapSeverity(v.Severity),
                Message: $"{v.PkgName} {v.InstalledVersion}: {v.Title}".Trim(),
                RuleId: v.VulnerabilityId,
                FilePath: r.Target,
                Line: null)))
            .ToList();
    }

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "CRITICAL" => FindingSeverity.Critical,
        "HIGH" => FindingSeverity.High,
        "MEDIUM" => FindingSeverity.Medium,
        "LOW" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record TrivyReport([property: JsonPropertyName("Results")] List<TrivyTarget>? Results);
    private sealed record TrivyTarget(
        [property: JsonPropertyName("Target")] string? Target,
        [property: JsonPropertyName("Vulnerabilities")] List<TrivyVuln>? Vulnerabilities);
    private sealed record TrivyVuln(
        [property: JsonPropertyName("VulnerabilityID")] string? VulnerabilityId,
        [property: JsonPropertyName("PkgName")] string? PkgName,
        [property: JsonPropertyName("InstalledVersion")] string? InstalledVersion,
        [property: JsonPropertyName("Severity")] string? Severity,
        [property: JsonPropertyName("Title")] string? Title);
}
```

- [ ] **Step 4: Tests grün**

Run: `dotnet test Naudit.slnx --filter TrivyAnalyzerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/TrivyAnalyzer.cs tests/Naudit.Tests/TrivyAnalyzerTests.cs
git commit -m "feat(sast): TrivyAnalyzer (SCA/Dependency-CVEs, JSON-Mapping)"
```

---

### Task 10: `DotnetScaAnalyzer` (opt-in)

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/DotnetScaAnalyzer.cs`
- Test: `tests/Naudit.Tests/DotnetScaAnalyzerTests.cs`

**Interfaces:**
- Produces: `DotnetScaAnalyzer(IProcessRunner, ILogger<DotnetScaAnalyzer>, TimeSpan timeout)`; `Name => "dotnet-sca"`. Läuft `dotnet restore` (Code-Ausführung!) dann `dotnet list package --vulnerable --include-transitive --format json` im Root.

- [ ] **Step 1: Failing Test schreiben**

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class DotnetScaAnalyzerTests
{
    private sealed class Ws(string root) : Naudit.Core.Abstractions.IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private const string ListJson = """
    { "projects": [
        { "path": "src/App.csproj",
          "frameworks": [
            { "framework": "net10.0",
              "transitivePackages": [
                { "id": "Some.Pkg", "resolvedVersion": "1.0.0",
                  "vulnerabilities": [ { "severity": "High", "advisoryurl": "https://ghsa/GHSA-xxxx" } ] } ] } ] } ] }
    """;

    [Fact]
    public async Task AnalyzeAsync_mapsVulnerablePackage_toScaFinding()
    {
        // restore → ok; list → JSON.
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("list")
                ? new ProcessResult(0, ListJson, "")
                : new ProcessResult(0, "", ""));
        var analyzer = new DotnetScaAnalyzer(runner, NullLogger<DotnetScaAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        var findings = await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []);

        var f = Assert.Single(findings);
        Assert.Equal("dotnet-sca", f.Tool);
        Assert.Equal(FindingCategory.Sca, f.Severity == FindingSeverity.High ? FindingCategory.Sca : f.Category);
        Assert.Equal(FindingSeverity.High, f.Severity);
        Assert.Contains("Some.Pkg", f.Message);
        Assert.Contains("1.0.0", f.Message);
        Assert.Equal("GHSA-xxxx", f.RuleId is null ? "GHSA-xxxx" : f.RuleId.Contains("GHSA-xxxx") ? "GHSA-xxxx" : f.RuleId);
    }

    [Fact]
    public async Task AnalyzeAsync_returnsEmpty_whenRestoreFails()
    {
        var runner = new StubProcessRunner(s =>
            string.Join(" ", s.Arguments).Contains("restore")
                ? new ProcessResult(1, "", "restore failed")
                : new ProcessResult(0, ListJson, ""));
        var analyzer = new DotnetScaAnalyzer(runner, NullLogger<DotnetScaAnalyzer>.Instance, TimeSpan.FromMinutes(5));

        Assert.Empty(await analyzer.AnalyzeAsync(new Ws("/tmp/x"), []));
    }
}
```

> Hinweis: Die zweite Assertion zu `RuleId` ist bewusst tolerant gehalten — die Implementierung leitet die RuleId aus `advisoryurl` ab (letztes Pfadsegment). Wenn dir das zu indirekt ist, ersetze die Zeile durch `Assert.Equal("GHSA-xxxx", f.RuleId);` nachdem du in der Implementierung das letzte URL-Segment als RuleId setzt.

- [ ] **Step 2: Test schlägt fehl**

Run: `dotnet test Naudit.slnx --filter DotnetScaAnalyzerTests`
Expected: FAIL.

- [ ] **Step 3: Implementierung schreiben**

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;

namespace Naudit.Infrastructure.Sast;

/// <summary>SCA für .NET: `dotnet restore` (baut/restored fremden Code!) dann
/// `dotnet list package --vulnerable --include-transitive --format json`. Opt-in. Fehler ⇒ leere Liste.</summary>
public sealed class DotnetScaAnalyzer(IProcessRunner runner, ILogger<DotnetScaAnalyzer> logger, TimeSpan timeout) : ISastAnalyzer
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public string Name => "dotnet-sca";

    public async Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        var restore = await RunDotnetAsync(workspace.RootPath, ct, "restore");
        if (restore is null || restore.ExitCode != 0)
        {
            logger.LogWarning("dotnet restore fehlgeschlagen (Exit {Code})", restore?.ExitCode);
            return [];
        }

        var list = await RunDotnetAsync(workspace.RootPath, ct,
            "list", "package", "--vulnerable", "--include-transitive", "--format", "json");
        if (list is null || list.ExitCode != 0 || string.IsNullOrWhiteSpace(list.StdOut))
        {
            logger.LogWarning("dotnet list package fehlgeschlagen (Exit {Code})", list?.ExitCode);
            return [];
        }

        DotnetListReport? report;
        try { report = JsonSerializer.Deserialize<DotnetListReport>(list.StdOut, JsonOpts); }
        catch (JsonException ex) { logger.LogWarning(ex, "dotnet list JSON nicht parsebar"); return []; }

        if (report?.Projects is null)
            return [];

        return (
            from p in report.Projects
            from fw in p.Frameworks ?? []
            from pkg in (fw.TopLevelPackages ?? []).Concat(fw.TransitivePackages ?? [])
            from v in pkg.Vulnerabilities ?? []
            select new ScanFinding(
                Tool: "dotnet-sca",
                Category: FindingCategory.Sca,
                Severity: MapSeverity(v.Severity),
                Message: $"{pkg.Id} {pkg.ResolvedVersion}: {v.Severity} vulnerability",
                RuleId: LastSegment(v.AdvisoryUrl),
                FilePath: p.Path,
                Line: null)).ToList();
    }

    private async Task<ProcessResult?> RunDotnetAsync(string root, CancellationToken ct, params string[] args)
    {
        var spec = new ProcessSpec("dotnet", args, StdIn: null, Environment: null, WorkingDirectory: root, Timeout: timeout);
        try { return await runner.RunAsync(spec, ct); }
        catch (Exception ex) { logger.LogWarning(ex, "dotnet nicht ausführbar"); return null; }
    }

    private static string? LastSegment(string? url)
        => string.IsNullOrEmpty(url) ? null : url.TrimEnd('/').Split('/').Last();

    private static FindingSeverity MapSeverity(string? s) => s?.ToUpperInvariant() switch
    {
        "CRITICAL" => FindingSeverity.Critical,
        "HIGH" => FindingSeverity.High,
        "MODERATE" => FindingSeverity.Medium,
        "LOW" => FindingSeverity.Low,
        _ => FindingSeverity.Info,
    };

    private sealed record DotnetListReport([property: JsonPropertyName("projects")] List<DotnetProject>? Projects);
    private sealed record DotnetProject(
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("frameworks")] List<DotnetFramework>? Frameworks);
    private sealed record DotnetFramework(
        [property: JsonPropertyName("topLevelPackages")] List<DotnetPackage>? TopLevelPackages,
        [property: JsonPropertyName("transitivePackages")] List<DotnetPackage>? TransitivePackages);
    private sealed record DotnetPackage(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("resolvedVersion")] string? ResolvedVersion,
        [property: JsonPropertyName("vulnerabilities")] List<DotnetVuln>? Vulnerabilities);
    private sealed record DotnetVuln(
        [property: JsonPropertyName("severity")] string? Severity,
        [property: JsonPropertyName("advisoryurl")] string? AdvisoryUrl);
}
```

- [ ] **Step 4: Test anpassen (RuleId exakt) + grün**

Setze in `DotnetScaAnalyzerTests` die RuleId-Assertion auf die jetzt eindeutige Form:

```csharp
        Assert.Equal("GHSA-xxxx", f.RuleId);
```

und die Category-Assertion klar auf:

```csharp
        Assert.Equal(FindingCategory.Sca, f.Category);
```

Run: `dotnet test Naudit.slnx --filter DotnetScaAnalyzerTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/DotnetScaAnalyzer.cs tests/Naudit.Tests/DotnetScaAnalyzerTests.cs
git commit -m "feat(sast): DotnetScaAnalyzer (opt-in, dotnet list --vulnerable)"
```

---

### Task 11: `SastOptions` + DI-Wiring

**Files:**
- Create: `src/Naudit.Infrastructure/Sast/SastOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Test: `tests/Naudit.Tests/SastWiringTests.cs`

**Interfaces:**
- Consumes: alle SAST-Typen aus Tasks 2–10.
- Produces: Registrierung von `IProcessRunner`→`SystemProcessRunner`, `IFindingReducer`→`DeterministicFindingReducer`, `IWorkspaceProvider`→`GitWorkspaceProvider` und je gewähltem Namen `ISastAnalyzer`.

- [ ] **Step 1: `SastOptions.cs` anlegen**

```csharp
namespace Naudit.Infrastructure.Sast;

public sealed class SastOptions
{
    /// <summary>SAST/SCA-Grounding global an/aus. Aus ⇒ exakt diff-only.</summary>
    public bool Enabled { get; set; }

    /// <summary>Aktive Analyzer per Name: "semgrep", "trivy", "dotnet-sca".</summary>
    public List<string> Analyzers { get; set; } = new() { "semgrep", "trivy" };

    /// <summary>Reducer-Strategie. Aktuell nur "deterministic" (Seam für späteres "llm").</summary>
    public string Reducer { get; set; } = "deterministic";

    /// <summary>Timeout je Analyzer/Tool-Aufruf.</summary>
    public TimeSpan AnalyzerTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cap pro Category in der Verdichtung.</summary>
    public int MaxFindingsPerGroup { get; set; } = 20;
}
```

- [ ] **Step 2: Failing Wiring-Test schreiben**

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Xunit;

namespace Naudit.Tests;

public class SastWiringTests
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
    public void Enabled_registersSelectedAnalyzers()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "true",
            ["Naudit:Sast:Analyzers:0"] = "semgrep",
            ["Naudit:Sast:Analyzers:1"] = "trivy",
        });

        using var scope = sp.CreateScope();
        var analyzers = scope.ServiceProvider.GetServices<ISastAnalyzer>().ToList();

        Assert.Equal(2, analyzers.Count);
        Assert.Contains(analyzers, a => a.Name == "semgrep");
        Assert.Contains(analyzers, a => a.Name == "trivy");
    }

    [Fact]
    public void Disabled_registersNoAnalyzers_butReviewServiceResolves()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:Sast:Enabled"] = "false",
        });

        using var scope = sp.CreateScope();
        Assert.Empty(scope.ServiceProvider.GetServices<ISastAnalyzer>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Naudit.Core.Review.ReviewService>());
    }
}
```

- [ ] **Step 3: Test schlägt fehl**

Run: `dotnet test Naudit.slnx --filter SastWiringTests`
Expected: FAIL (keine Registrierung).

- [ ] **Step 4: DI erweitern**

In `DependencyInjection.cs` die `using`-Zeilen ergänzen:

```csharp
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Sast;
```

Und vor `services.AddScoped<ReviewService>();` einfügen:

```csharp
        // SAST/SCA-Grounding: immer die Infrastruktur-Naht registrieren (harmlos wenn ungenutzt),
        // Analyzer nur bei Enabled. Ohne Analyzer verhält sich ReviewService exakt diff-only.
        var sastOptions = configuration.GetSection("Naudit:Sast").Get<SastOptions>() ?? new SastOptions();
        services.AddSingleton<IProcessRunner, SystemProcessRunner>();
        services.AddSingleton<IFindingReducer>(_ => new DeterministicFindingReducer(sastOptions.MaxFindingsPerGroup));
        services.AddScoped<IWorkspaceProvider, GitWorkspaceProvider>();

        if (sastOptions.Enabled)
        {
            foreach (var name in sastOptions.Analyzers)
            {
                switch (name.ToLowerInvariant())
                {
                    case "semgrep":
                        services.AddScoped<ISastAnalyzer>(sp => new SemgrepAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SemgrepAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    case "trivy":
                        services.AddScoped<ISastAnalyzer>(sp => new TrivyAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<TrivyAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                    case "dotnet-sca":
                        services.AddScoped<ISastAnalyzer>(sp => new DotnetScaAnalyzer(
                            sp.GetRequiredService<IProcessRunner>(),
                            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DotnetScaAnalyzer>(),
                            sastOptions.AnalyzerTimeout));
                        break;
                }
            }
        }
```

Ergänze oben in `DependencyInjection.cs` `using Microsoft.Extensions.Logging;` (für `CreateLogger<T>`/`ILoggerFactory`).

- [ ] **Step 5: Tests grün (inkl. voller Suite)**

Run: `dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Sast/SastOptions.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/SastWiringTests.cs
git commit -m "feat(sast): SastOptions + DI-Wiring (config-gewählte Analyzer)"
```

---

### Task 12: Dockerfile — Semgrep + Trivy im Runtime-Image

Damit Naudit die Tools tatsächlich aufrufen kann. `dotnet-sca` braucht zusätzlich das SDK (nicht im aspnet-Runtime-Image) und bleibt daher opt-in; siehe Doku-Hinweis.

**Files:**
- Modify: `Dockerfile`

- [ ] **Step 1: Runtime-Stage um Tool-Installation ergänzen**

In `Dockerfile` nach `WORKDIR /app` (in der Runtime-Stage, vor `COPY --from=build`) einfügen:

```dockerfile
# SAST/SCA-Tools: Trivy (Binary) + Semgrep (pip). Als root installieren, dann auf non-root wechseln.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl python3 python3-pip \
 && curl -sfL https://raw.githubusercontent.com/aquasecurity/trivy/main/contrib/install.sh | sh -s -- -b /usr/local/bin \
 && pip3 install --no-cache-dir --break-system-packages semgrep \
 && apt-get purge -y curl && apt-get autoremove -y \
 && rm -rf /var/lib/apt/lists/*
```

- [ ] **Step 2: Image baut**

Run: `docker build -t naudit:sast-test .`
Expected: Build PASS; `docker run --rm naudit:sast-test sh -c "trivy --version && semgrep --version"` zeigt beide Versionen.

> Falls `docker` lokal nicht verfügbar ist: diesen Schritt überspringen und im Commit vermerken, dass der Image-Build in CI verifiziert wird. Die App-Tests (`dotnet test`) sind davon unberührt.

- [ ] **Step 3: Commit**

```bash
git add Dockerfile
git commit -m "build(docker): Semgrep + Trivy ins Runtime-Image (SAST/SCA-Tools)"
```

---

### Task 13: Doku + Config-Beispiel + CLAUDE.md

**Files:**
- Create: `docs/sast-grounding.md`
- Modify: `src/Naudit.Web/appsettings.json`, `CLAUDE.md`

- [ ] **Step 1: `appsettings.json` um den `Sast`-Block ergänzen**

Im `Naudit`-Objekt (z. B. nach `"Review"`) einfügen:

```json
    "Sast": {
      "Enabled": false,
      "Analyzers": [ "semgrep", "trivy" ],
      "Reducer": "deterministic",
      "AnalyzerTimeout": "00:05:00",
      "MaxFindingsPerGroup": 20
    }
```

- [ ] **Step 2: `docs/sast-grounding.md` schreiben** (Englisch)

```markdown
# SAST/SCA grounding

Naudit can clone the MR/PR head and run static-analysis (SAST) and dependency
(SCA) scanners, then feed the normalized findings into the review prompt as
**grounding**. The LLM still produces the single verdict — tools never block on
their own (no hard tool gate).

## Configuration (`Naudit:Sast`)

| Key | Default | Meaning |
| --- | --- | --- |
| `Enabled` | `false` | Master switch. `false` ⇒ exact diff-only behavior. |
| `Analyzers` | `["semgrep","trivy"]` | Active analyzers by name. |
| `Reducer` | `deterministic` | Finding de-duplication strategy (seam for a future `llm` reducer). |
| `AnalyzerTimeout` | `00:05:00` | Per-tool timeout. |
| `MaxFindingsPerGroup` | `20` | Cap per category after sorting. |

## Analyzers

- **semgrep** — SAST, multi-language, no build (does not execute repo code).
- **trivy** — SCA/dependency CVEs, multi-ecosystem, no build.
- **dotnet-sca** — `.NET` SCA via `dotnet list package --vulnerable`. **Opt-in:**
  it runs `dotnet restore`, which **executes the reviewed code's build logic**,
  and it needs the .NET SDK in the image (the default runtime image only ships
  semgrep + trivy). Enable only for trusted repos and an SDK-based image.

## Behavior

- All findings are included **repo-wide**, annotated `[in diff]` (touched by the
  MR) vs `[pre-existing]`.
- Findings are de-duplicated, sorted by severity then in-diff, and capped per
  category before grounding.
- Graceful degradation: a single analyzer failure is logged and skipped; a failed
  checkout degrades the review to diff-only (it does not fail the gate).
- The system prompt instructs the model to treat the toolchain/target framework
  as valid and current (mitigates outdated-knowledge false positives).

## Prerequisites

The image must provide `semgrep` and `trivy` on `PATH` (see `Dockerfile`). Naudit
clones via the platform token it already holds; no extra credentials needed.
```

- [ ] **Step 3: `CLAUDE.md` Extension-Point ergänzen**

Unter „### Extension points (do not break the Core rule)" einen Punkt anfügen:

```markdown
- **New SAST/SCA analyzer:** implement `ISastAnalyzer` in
  `src/Naudit.Infrastructure/Sast/`, map the tool's output to `ScanFinding`, and
  add a `case` in the analyzer-selection `switch` in `DependencyInjection.cs`.
  Selection is config-only via `Naudit:Sast:Analyzers`. No change to Core. The
  findings are fed to the LLM as grounding (`PromptBuilder`); the verdict stays
  LLM-only.
```

- [ ] **Step 4: Build/Test als Schlusskontrolle**

Run: `dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add docs/sast-grounding.md src/Naudit.Web/appsettings.json CLAUDE.md
git commit -m "docs(sast): Grounding-Doku, appsettings-Beispiel, CLAUDE.md-Extension-Point"
```

---

## Self-Review

**1. Spec coverage:**
- Naudit klont selbst → Task 6 (`GetCheckoutAsync`) + Task 7 (`GitWorkspaceProvider`). ✓
- Pluggbare Analyzer (Semgrep/Trivy/dotnet-sca) → Tasks 8/9/10 + `ISastAnalyzer` (Task 2). ✓
- Grounding-only (LLM-Verdict) → Task 5 (Verdict-Mapping unverändert), Task 4 (Prompt). ✓
- Funde repo-weit + `[in diff]`/`[pre-existing]` → Task 4 (Annotation-Rendering) + Task 5 (InDiff-Annotation). ✓
- Deterministische Verdichtung + Seam → Task 3 (`DeterministicFindingReducer`), `IFindingReducer` (Task 2), Config `Reducer` (Task 11). ✓
- Toolchain-Grounding gegen .NET-10-Halluzination → Task 4 (System-Prompt). ✓
- `IProcessRunner` wiederverwendet/verschoben → Task 1. ✓
- Rückwärtskompatibel (diff-only) → Task 5 (`_analyzers.Count == 0`), Task 11 (Disabled-Test). ✓
- Graceful degradation → Task 5 (checkout/analyzer try-catch). ✓
- Config `Naudit:Sast` → Task 11 + Task 13. ✓
- Container-Tools → Task 12. ✓
- Caveats (dotnet-sca baut Code; SDK nötig) → Task 13 (Doku). ✓

**2. Placeholder scan:** Kein „TBD"/„später"; jeder Code-Schritt zeigt vollständigen Code; Test-JSON ist real. Task 1/6 enthalten „Compiler-getriebene using-Fixes" — das ist eine konkrete, deterministische Anweisung für einen mechanischen Namespace-Move, kein Platzhalter.

**3. Type consistency:** `ScanFinding`-Felder, `FindingCategory`/`FindingSeverity`, `RepoCheckoutInfo(CloneUrl, HeadRef)`, `ProcessSpec`-Argliste, `ISastAnalyzer.AnalyzeAsync(IReviewWorkspace, IReadOnlyList<CodeChange>, CancellationToken)`, `IFindingReducer.ReduceAsync(...)`, Analyzer-`Name`-Strings (`"semgrep"`/`"trivy"`/`"dotnet-sca"`) sind über Tasks 2–13 konsistent. `PromptBuilder.Build(..., findings = null)` wird in Task 4 eingeführt und in Task 5 mit Argument genutzt. ✓
