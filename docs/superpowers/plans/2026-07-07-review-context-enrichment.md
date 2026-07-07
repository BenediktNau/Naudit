# Review Context Enrichment (workspace-based) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the reviewer LLM surrounding code, call-sites of changed symbols, and a repo overview — cut deterministically from the shallow checkout Naudit already makes — so it can judge *what a change does* and *how it fits the flow*, without the whole codebase in the prompt and without any tooling in the target repo.

**Architecture:** Same seam pattern as `IPromptRedactor`/`ISastAnalyzer`: a Core interface `IContextCollector` + Core model `ReviewContext`, one Infrastructure implementation `WorkspaceContextCollector` reading the checked-out tree, selected/configured in `AddNauditInfrastructure`. `ReviewService` performs **one** checkout shared by SAST and context; the collected context is redacted (like diffs/findings/title) and rendered by `PromptBuilder` as a bounded "Repository context" section. Core keeps depending on MEAI abstractions only.

**Tech Stack:** .NET 10, xUnit; regex + indentation heuristics (language-agnostic); no new NuGet packages.

## Global Constraints

- **Core rule:** `Naudit.Core` depends only on `Microsoft.Extensions.AI.Abstractions`. `IContextCollector`, `ReviewContext`, `FileEnvironment`, `SymbolUsage`, `ReviewContextOptions` live in Core; the implementation `WorkspaceContextCollector` lives in Infrastructure. No git/SDK types in Core.
- **Solution file is `Naudit.slnx`** — `dotnet test Naudit.slnx` (never `Naudit.sln`).
- **Code comments are in German.** Docs (`docs/**`, `README`) are in English.
- **TDD:** red → green → one commit per task.
- **Behaviour toggle:** context is **on by default** (`Naudit:Review:Context:Enabled=true`). With it off *and* no SAST analyzers, `ReviewService` must not check out (identical to today's diff-only path and prompt).
- **Fail-open:** a checkout failure or a collector exception degrades to diff-only; it never fails the review. Cancellation still propagates.
- **Determinism:** same workspace + changes → same `ReviewContext` (sorted enumeration, stable budgeting).

---

### Task 1: Core seam — types + options + config binding

**Files:**
- Create: `src/Naudit.Core/Abstractions/IContextCollector.cs`
- Create: `src/Naudit.Core/Models/ReviewContext.cs`
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs` (add `ReviewContextOptions` + `Context` property)
- Test: `tests/Naudit.Tests/ReviewContextOptionsTests.cs`

**Interfaces:**
- Produces:
  - `interface IContextCollector { Task<ReviewContext> CollectAsync(IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default); }`
  - `record ReviewContext(IReadOnlyList<FileEnvironment> Environments, IReadOnlyList<SymbolUsage> Usages, string? Overview)` with `static readonly ReviewContext Empty`
  - `record FileEnvironment(string FilePath, int StartLine, string Content, bool IsFullFile)`
  - `record SymbolUsage(string Symbol, string FilePath, int Line, string Snippet)`
  - `class ReviewContextOptions { bool Enabled; int MaxChars; int FullFileMaxLines; int BlockPadLines; int UsageSnippetLines; int MaxUsagesPerSymbol; int MaxTreeDepth; int ReadmeMaxLines; }`
  - `ReviewOptions.Context` (type `ReviewContextOptions`)

- [ ] **Step 1: Write the failing test**

Create `tests/Naudit.Tests/ReviewContextOptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class ReviewContextOptionsTests
{
    [Fact]
    public void Defaults_areOn_withModerateBudget()
    {
        var ctx = new ReviewOptions().Context;

        Assert.True(ctx.Enabled);
        Assert.Equal(40_000, ctx.MaxChars);
        Assert.Equal(400, ctx.FullFileMaxLines);
        Assert.Equal(30, ctx.BlockPadLines);
        Assert.Equal(3, ctx.UsageSnippetLines);
        Assert.Equal(5, ctx.MaxUsagesPerSymbol);
        Assert.Equal(3, ctx.MaxTreeDepth);
        Assert.Equal(50, ctx.ReadmeMaxLines);
    }

    [Fact]
    public void Context_bindsFromConfiguration_underNauditReview()
    {
        // Genau der Bindungs-Pfad aus AddNauditInfrastructure: GetSection("Naudit:Review").Get<ReviewOptions>().
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Naudit:Review:Context:Enabled"] = "false",
                ["Naudit:Review:Context:MaxChars"] = "12345",
                ["Naudit:Review:Context:FullFileMaxLines"] = "200",
            })
            .Build();

        var options = config.GetSection("Naudit:Review").Get<ReviewOptions>()!;

        Assert.False(options.Context.Enabled);
        Assert.Equal(12345, options.Context.MaxChars);
        Assert.Equal(200, options.Context.FullFileMaxLines);
    }

    [Fact]
    public void ReviewContext_Empty_isEmpty()
    {
        var empty = Naudit.Core.Models.ReviewContext.Empty;

        Assert.Empty(empty.Environments);
        Assert.Empty(empty.Usages);
        Assert.Null(empty.Overview);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewContextOptionsTests`
Expected: FAIL — compile error (`ReviewContextOptions`, `Context`, `ReviewContext` do not exist).

- [ ] **Step 3: Create the Core model file**

Create `src/Naudit.Core/Models/ReviewContext.cs`:

```csharp
namespace Naudit.Core.Models;

/// <summary>Read-only-Kontext zu einer Änderung: umgebender Code, Call-Sites, Repo-Überblick.</summary>
public sealed record ReviewContext(
    IReadOnlyList<FileEnvironment> Environments,
    IReadOnlyList<SymbolUsage> Usages,
    string? Overview)
{
    /// <summary>Leerer Kontext (Feature aus / Checkout fehlgeschlagen / kein Fund) — rendert nichts.</summary>
    public static readonly ReviewContext Empty = new([], [], null);
}

/// <summary>Umgebender Code einer geänderten Datei: ganze Datei oder ein Block-Ausschnitt ab StartLine.</summary>
public sealed record FileEnvironment(string FilePath, int StartLine, string Content, bool IsFullFile);

/// <summary>Eine Verwendungsstelle eines im Diff deklarierten Symbols (1-basierte Zeile).</summary>
public sealed record SymbolUsage(string Symbol, string FilePath, int Line, string Snippet);
```

- [ ] **Step 4: Create the Core interface file**

Create `src/Naudit.Core/Abstractions/IContextCollector.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Schneidet Review-Kontext (Umgebung, Call-Sites, Repo-Überblick) aus dem ausgecheckten Quellbaum.</summary>
public interface IContextCollector
{
    Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

- [ ] **Step 5: Add the options to `ReviewOptions.cs`**

Modify `src/Naudit.Core/Review/ReviewOptions.cs` — add the `Context` property to `ReviewOptions` and the new options class. Full file after edit:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Review;

public sealed class ReviewOptions
{
    public string SystemPrompt { get; set; } = PromptBuilder.DefaultSystemPrompt;

    /// <summary>Severity-bewusste Gate-Policy (Naudit:Review:Gate).</summary>
    public ReviewGateOptions Gate { get; set; } = new();

    /// <summary>Kontext-Anreicherung aus dem Checkout (Naudit:Review:Context).</summary>
    public ReviewContextOptions Context { get; set; } = new();
}

/// <summary>Ab wann ein Review blockt (request_changes). Default: nur bestätigtes High/Critical.</summary>
public sealed class ReviewGateOptions
{
    /// <summary>Mindest-Schweregrad, ab dem ein Fund blocken kann. Default High.</summary>
    public FindingSeverity MinSeverity { get; set; } = FindingSeverity.High;

    /// <summary>Mindest-Sicherheit, ab der ein Fund blocken kann. Default Medium.</summary>
    public ReviewConfidence MinConfidence { get; set; } = ReviewConfidence.Medium;
}

/// <summary>Steuert die Kontext-Anreicherung: Umfang, Budget, Heuristik-Grenzen. Default AN.</summary>
public sealed class ReviewContextOptions
{
    /// <summary>Kontext-Sektion überhaupt bauen. Default true; false ⇒ heutiges diff-only-Verhalten.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gesamtbudget (Zeichen) für die Kontext-Sektion. Priorität Umgebung &gt; Call-Sites &gt; Überblick.</summary>
    public int MaxChars { get; set; } = 40_000;

    /// <summary>Datei ≤ dieser Zeilenzahl ⇒ ganze Datei; größer ⇒ Block-Heuristik um die Hunks.</summary>
    public int FullFileMaxLines { get; set; } = 400;

    /// <summary>Mindest-Fallback-Fenster ± Zeilen um einen Hunk-Anker, falls die Block-Heuristik zu eng ist.</summary>
    public int BlockPadLines { get; set; } = 30;

    /// <summary>± Zeilen Umgebung um eine Call-Site.</summary>
    public int UsageSnippetLines { get; set; } = 3;

    /// <summary>Maximale Call-Sites je Symbol.</summary>
    public int MaxUsagesPerSymbol { get; set; } = 5;

    /// <summary>Tiefe des Verzeichnisbaums im Überblick.</summary>
    public int MaxTreeDepth { get; set; } = 3;

    /// <summary>Kopf-Zeilen der README im Überblick.</summary>
    public int ReadmeMaxLines { get; set; } = 50;
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewContextOptionsTests`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Core/Abstractions/IContextCollector.cs src/Naudit.Core/Models/ReviewContext.cs src/Naudit.Core/Review/ReviewOptions.cs tests/Naudit.Tests/ReviewContextOptionsTests.cs
git commit -m "feat(context): Core-Naht IContextCollector + ReviewContext + ReviewContextOptions"
```

---

### Task 2: WorkspaceContextCollector — surrounding-code extraction

**Files:**
- Create: `src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs`
- Test: `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs`

**Interfaces:**
- Consumes: `IContextCollector`, `ReviewContext`/`FileEnvironment`/`SymbolUsage`, `ReviewContextOptions` (Task 1); `IReviewWorkspace`, `CodeChange`, `DiffParser` (existing Core).
- Produces: `class WorkspaceContextCollector(ReviewContextOptions options) : IContextCollector`. After this task `CollectAsync` fills `Environments` (full file ≤ `FullFileMaxLines`, else indentation-block excerpts); `Usages` empty, `Overview` null.

- [ ] **Step 1: Write the failing tests**

Create `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;
using Naudit.Infrastructure.Context;
using Xunit;

namespace Naudit.Tests;

public class WorkspaceContextCollectorTests
{
    // ---- Fixtures ---------------------------------------------------------
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "naudit-ctx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteFile(string root, string rel, string content)
    {
        var abs = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, content.Replace("\r\n", "\n"));
    }

    private sealed class TestWorkspace(string root) : IReviewWorkspace
    {
        public string RootPath { get; } = root;
        public ValueTask DisposeAsync()
        {
            try { Directory.Delete(RootPath, recursive: true); } catch { /* best effort */ }
            return ValueTask.CompletedTask;
        }
    }

    // ---- Tests ------------------------------------------------------------
    [Fact]
    public async Task Collect_smallChangedFile_includesWholeFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "src/Foo.cs", "class Foo\n{\n    void A() { return; }\n}\n");
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -3,0 +3,1 @@\n+    void A() { return; }") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.Equal("src/Foo.cs", env.FilePath);
        Assert.True(env.IsFullFile);
        Assert.Equal(1, env.StartLine);
        Assert.Contains("class Foo", env.Content);
        Assert.Contains("void A()", env.Content);
    }

    [Fact]
    public async Task Collect_largeChangedFile_extractsEnclosingBlock_notWholeFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        // 11 Zeilen, zwei Methoden. Anker = New-File-Zeile 6 (DoThing im Body von A()).
        WriteFile(root, "src/Foo.cs",
            "namespace N;\n" +      // 1
            "class C\n" +           // 2
            "{\n" +                 // 3
            "    void A()\n" +      // 4
            "    {\n" +             // 5
            "        DoThing();\n" +// 6  <- Anker
            "    }\n" +             // 7
            "    void B()\n" +      // 8
            "    {\n" +             // 9
            "    }\n" +             // 10
            "}\n");                 // 11
        var changes = new[] { new CodeChange("src/Foo.cs", "@@ -6,0 +6,1 @@\n+        DoThing();") };
        // FullFileMaxLines klein ⇒ Block-Modus; BlockPadLines 0 ⇒ reine Einrückungs-Heuristik.
        var opts = new ReviewContextOptions { FullFileMaxLines = 5, BlockPadLines = 0 };
        var sut = new WorkspaceContextCollector(opts);

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.False(env.IsFullFile);
        Assert.Equal(4, env.StartLine);              // Blockkopf "void A()"
        Assert.Contains("void A()", env.Content);
        Assert.Contains("DoThing();", env.Content);
        Assert.DoesNotContain("void B()", env.Content);   // Nachbar-Block bleibt draußen
    }

    [Fact]
    public async Task Collect_deletedFile_missingInWorkspace_isSkipped()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        // Datei existiert NICHT im Checkout (gelöscht) -> kein Environment, kein Crash.
        var changes = new[] { new CodeChange("gone.cs", "@@ -1,1 +0,0 @@\n-was here") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Empty(ctx.Environments);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: FAIL — `WorkspaceContextCollector` does not exist.

- [ ] **Step 3: Create `WorkspaceContextCollector` with surrounding-code extraction**

Create `src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs`:

```csharp
using System.Text.RegularExpressions;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Core.Review;

namespace Naudit.Infrastructure.Context;

/// <summary>Sprachagnostischer Kontext-Sammler: schneidet umgebenden Code, Call-Sites und einen
/// Repo-Überblick aus dem ausgecheckten Baum. Regex + Einrückung, Precision vor Recall.
/// Fehler je Datei werden geschluckt (weiter mit der nächsten); der Aufrufer degradiert bei
/// Gesamtfehler auf diff-only.</summary>
public sealed class WorkspaceContextCollector(ReviewContextOptions options) : IContextCollector
{
    // Obergrenze, wie weit die Block-Heuristik pro Richtung scannt (Schutz vor Runaway).
    private const int BlockScanLimit = 400;

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2); // ReDoS-Schutz (wie PatternRedactor)

    private static Regex R(string pattern)
        => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, RegexTimeout);

    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = workspace.RootPath;

        var environments = CollectEnvironments(root, changes);

        var ctx = new ReviewContext(environments, [], null);
        return Task.FromResult(ctx);
    }

    // ---- Umgebung ---------------------------------------------------------
    private IReadOnlyList<FileEnvironment> CollectEnvironments(string root, IReadOnlyList<CodeChange> changes)
    {
        var parsed = DiffParser.Parse(changes);
        var result = new List<FileEnvironment>();

        foreach (var change in changes)
        {
            var abs = SafeResolve(root, change.FilePath);
            if (abs is null || !File.Exists(abs))
                continue;                       // gelöschte/außerhalb liegende Datei -> überspringen

            string[] lines;
            try { lines = File.ReadAllLines(abs); }
            catch { continue; }                 // binär/unlesbar -> überspringen

            if (lines.Length <= options.FullFileMaxLines)
            {
                result.Add(new FileEnvironment(change.FilePath, 1, string.Join('\n', lines), IsFullFile: true));
                continue;
            }

            // Große Datei: Anker = hinzugefügte New-File-Zeilen aus dem Diff (Map-Wert null).
            var anchors = parsed.TryGetValue(change.FilePath, out var map)
                ? map.Where(kv => kv.Value is null).Select(kv => kv.Key).OrderBy(x => x).ToList()
                : [];
            foreach (var (start, end) in MergeRanges(anchors.Select(a => ExpandOne(lines, a)).ToList()))
            {
                var slice = string.Join('\n', lines[(start - 1)..end]);
                result.Add(new FileEnvironment(change.FilePath, start, slice, IsFullFile: false));
            }
        }

        return result;
    }

    // Erweitert einen Anker auf den umgebenden Block: rückwärts zur nächsten, weniger eingerückten
    // Deklarationszeile (Blockkopf), vorwärts bis die Einrückung wieder auf dieses Niveau fällt.
    private (int Start, int End) ExpandOne(string[] lines, int anchor)
    {
        int idx = Math.Clamp(anchor - 1, 0, lines.Length - 1);
        int baseIndent = IndentOf(lines[idx]);

        int start = idx;
        for (int i = idx - 1; i >= 0 && idx - i <= BlockScanLimit; i--)
        {
            if (lines[i].Trim().Length == 0) continue;
            int ind = IndentOf(lines[i]);
            if (ind < baseIndent && LooksLikeDeclaration(lines[i]))
            {
                start = i;
                baseIndent = ind;
                break;
            }
        }

        int end = idx;
        for (int i = idx + 1; i < lines.Length && i - idx <= BlockScanLimit; i++)
        {
            if (lines[i].Trim().Length == 0) { end = i; continue; }
            if (IndentOf(lines[i]) <= baseIndent) { end = i; break; }
            end = i;
        }

        // Mindest-Fallback-Fenster ± BlockPadLines um den Anker (0 ⇒ reine Heuristik).
        start = Math.Min(start, Math.Max(0, idx - options.BlockPadLines));
        end = Math.Max(end, Math.Min(lines.Length - 1, idx + options.BlockPadLines));
        return (start + 1, end + 1);            // 1-basiert, inklusiv
    }

    private static IReadOnlyList<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return ranges;
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int End)> { ranges[0] };
        foreach (var r in ranges.Skip(1))
        {
            var last = merged[^1];
            if (r.Start <= last.End + 1)
                merged[^1] = (last.Start, Math.Max(last.End, r.End));
            else
                merged.Add(r);
        }
        return merged;
    }

    private static int IndentOf(string line)
    {
        int n = 0;
        while (n < line.Length && (line[n] == ' ' || line[n] == '\t')) n++;
        return n;
    }

    private static bool LooksLikeDeclaration(string line)
        => DeclarationPatterns.Any(rx => rx.IsMatch(line));

    // ---- Symbol-Deklarationsmuster (auch für Blockkopf-Erkennung) --------
    // Keyword-Deklaration (def/function/func/fn/sub NAME), Typ-Deklaration
    // (class/interface/struct/record/enum/trait NAME) und C-Familien-Signatur (… NAME(...) {?).
    // Gruppe "name" = deklarierter Bezeichner. Timeout schützt die lazy C-Signatur vor ReDoS.
    private static readonly Regex[] DeclarationPatterns =
    [
        R(@"\b(?:def|function|func|fn|sub)\s+(?<name>[A-Za-z_]\w*)"),
        R(@"\b(?:class|interface|struct|record|enum|trait)\s+(?<name>[A-Za-z_]\w*)"),
        R(@"^[\w\s,<>\[\]\.\?]*?\b(?<name>[A-Za-z_]\w*)\s*\([^;]*\)\s*\{?\s*$"),
    ];

    // ---- Pfad-Sicherheit --------------------------------------------------
    // Verhindert Ausbruch aus dem Checkout (z. B. "../../etc/passwd" im Diff-Pfad).
    private static string? SafeResolve(string root, string relPath)
    {
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, relPath));
        return full.StartsWith(rootFull, StringComparison.Ordinal) ? full : null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs tests/Naudit.Tests/WorkspaceContextCollectorTests.cs
git commit -m "feat(context): WorkspaceContextCollector — umgebender Code (ganze Datei / Block-Heuristik)"
```

---

### Task 3: WorkspaceContextCollector — call-site extraction

**Files:**
- Modify: `src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs`
- Test: `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs` (add tests)

**Interfaces:**
- Consumes: everything from Task 2.
- Produces: `CollectAsync` now fills `Usages`: symbols declared on added (`+`) diff lines, searched word-boundary across workspace source files (excluding the declaring files and vendor/build dirs), ≤ `MaxUsagesPerSymbol` sites, ± `UsageSnippetLines` lines each.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task Collect_findsCallSites_ofNewlyDeclaredSymbol_excludingDeclaringFile()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "service.py", "def process_order(order):\n    return order\n");
        WriteFile(root, "caller.py", "def main():\n    result = process_order(o)\n    return result\n");
        // Änderung deklariert process_order in service.py.
        var changes = new[] { new CodeChange("service.py", "@@ -1,0 +1,1 @@\n+def process_order(order):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        var usage = Assert.Single(ctx.Usages);
        Assert.Equal("process_order", usage.Symbol);
        Assert.Equal("caller.py", usage.FilePath);       // Deklarationsdatei ausgeschlossen
        Assert.Equal(2, usage.Line);
        Assert.Contains("process_order(o)", usage.Snippet);
    }

    [Fact]
    public async Task Collect_capsUsagesPerSymbol()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "svc.py", "def widget(x):\n    return x\n");
        // Fünf Aufrufe, MaxUsagesPerSymbol=2 ⇒ höchstens 2 Fundstellen.
        WriteFile(root, "a.py", "widget(1)\nwidget(2)\nwidget(3)\nwidget(4)\nwidget(5)\n");
        var changes = new[] { new CodeChange("svc.py", "@@ -1,0 +1,1 @@\n+def widget(x):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { MaxUsagesPerSymbol = 2 });

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Equal(2, ctx.Usages.Count);
    }

    [Fact]
    public async Task Collect_ignoresVendorDirs()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "svc.py", "def gadget(x):\n    return x\n");
        WriteFile(root, "node_modules/dep.py", "gadget(99)\n");   // in Vendor-Dir -> ignoriert
        var changes = new[] { new CodeChange("svc.py", "@@ -1,0 +1,1 @@\n+def gadget(x):") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions());

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.Empty(ctx.Usages);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: FAIL — new usage tests fail (`Usages` still empty).

- [ ] **Step 3: Add call-site extraction**

In `WorkspaceContextCollector.cs`, change `CollectAsync` to also fill usages:

```csharp
    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = workspace.RootPath;

        var environments = CollectEnvironments(root, changes);
        var usages = CollectUsages(root, changes);

        var ctx = new ReviewContext(environments, usages, null);
        return Task.FromResult(ctx);
    }
```

Add these members (after `CollectEnvironments` / before `SafeResolve`):

```csharp
    // ---- Call-Sites -------------------------------------------------------
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "using", "foreach", "lock",
        "return", "new", "else", "do", "try", "finally", "await", "throw",
    };

    private static readonly HashSet<string> ExcludedDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", "dist", "build", "target",
        "vendor", "packages", ".vs", ".idea",
    };

    private const long MaxFileBytes = 512 * 1024;

    private IReadOnlyList<SymbolUsage> CollectUsages(string root, IReadOnlyList<CodeChange> changes)
    {
        var symbols = ExtractSymbols(changes);
        if (symbols.Count == 0) return [];

        var declaringFiles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            var abs = SafeResolve(root, c.FilePath);
            if (abs is not null) declaringFiles.Add(abs);
        }

        var files = EnumerateSourceFiles(root).ToList();     // deterministisch sortiert
        var usages = new List<SymbolUsage>();

        foreach (var symbol in symbols)
        {
            var rx = R($@"\b{Regex.Escape(symbol)}\b");
            int found = 0;
            foreach (var abs in files)
            {
                if (found >= options.MaxUsagesPerSymbol) break;
                if (declaringFiles.Contains(abs)) continue;   // Deklarationsdatei überspringen

                string[] lines;
                try { lines = File.ReadAllLines(abs); }
                catch { continue; }

                for (int i = 0; i < lines.Length && found < options.MaxUsagesPerSymbol; i++)
                {
                    if (!rx.IsMatch(lines[i])) continue;
                    int lo = Math.Max(0, i - options.UsageSnippetLines);
                    int hi = Math.Min(lines.Length - 1, i + options.UsageSnippetLines);
                    var snippet = string.Join('\n', lines[lo..(hi + 1)]);
                    var rel = Path.GetRelativePath(root, abs).Replace('\\', '/');
                    usages.Add(new SymbolUsage(symbol, rel, i + 1, snippet));
                    found++;
                }
            }
        }

        return usages;
    }

    // Zieht Bezeichner aus hinzugefügten (+) Diff-Zeilen über den Deklarations-Regex-Katalog.
    private static IReadOnlyList<string> ExtractSymbols(IReadOnlyList<CodeChange> changes)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            foreach (var raw in change.Diff.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] != '+' || line.StartsWith("+++", StringComparison.Ordinal))
                    continue;
                var added = line[1..];
                foreach (var rx in DeclarationPatterns)
                {
                    var m = rx.Match(added);
                    if (!m.Success) continue;
                    var name = m.Groups["name"].Value;
                    if (name.Length >= 3 && !Keywords.Contains(name))
                        names.Add(name);
                }
            }
        }
        return names.ToList();
    }

    // Rekursiver, deterministisch sortierter Datei-Walk unter Auslassung von Vendor-/Build-Dirs
    // und zu großer/binärer Dateien.
    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<string> subdirs, files;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir)
                    .Where(d => !ExcludedDirs.Contains(Path.GetFileName(d)))
                    .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();  // Stack ⇒ absteigend rein = aufsteigend raus
                files = Directory.EnumerateFiles(dir)
                    .OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
            }
            catch { continue; }

            foreach (var sub in subdirs) stack.Push(sub);
            foreach (var f in files)
            {
                long len;
                try { len = new FileInfo(f).Length; } catch { continue; }
                if (len > 0 && len <= MaxFileBytes) yield return f;
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs tests/Naudit.Tests/WorkspaceContextCollectorTests.cs
git commit -m "feat(context): Call-Sites geänderter Symbole (Regex-Katalog + Wortgrenzen-Suche, Vendor-Dirs aus)"
```

---

### Task 4: WorkspaceContextCollector — repo overview + budget

**Files:**
- Modify: `src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs`
- Test: `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs` (add tests)

**Interfaces:**
- Consumes: everything from Tasks 2–3.
- Produces: `CollectAsync` now fills `Overview` (directory tree depth ≤ `MaxTreeDepth` + first `ReadmeMaxLines` of a root `README.*`) and applies the `MaxChars` budget in priority order (environments → usages → overview) with a `[truncated by budget]` marker.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Naudit.Tests/WorkspaceContextCollectorTests.cs`:

```csharp
    [Fact]
    public async Task Collect_overview_hasTree_andReadmeHead()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "README.md", "# MyProject\nDoes the thing.\nSecond line.\n");
        WriteFile(root, "src/App.cs", "class App { }\n");
        var changes = new[] { new CodeChange("src/App.cs", "@@ -1,0 +1,1 @@\n+class App { }") };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { ReadmeMaxLines = 2 });

        var ctx = await sut.CollectAsync(ws, changes);

        Assert.NotNull(ctx.Overview);
        Assert.Contains("src/", ctx.Overview!);            // Verzeichnisbaum
        Assert.Contains("App.cs", ctx.Overview!);
        Assert.Contains("# MyProject", ctx.Overview!);     // README-Kopf
        Assert.DoesNotContain("Second line.", ctx.Overview!);  // durch ReadmeMaxLines gedeckelt
    }

    [Fact]
    public async Task Collect_budget_truncatesAndMarks()
    {
        var root = NewTempDir();
        await using var ws = new TestWorkspace(root);
        WriteFile(root, "big.txt", new string('x', 200) + "\n");
        var changes = new[] { new CodeChange("big.txt", "@@ -1,0 +1,1 @@\n+" + new string('x', 200)) };
        var sut = new WorkspaceContextCollector(new ReviewContextOptions { MaxChars = 50 });

        var ctx = await sut.CollectAsync(ws, changes);

        var env = Assert.Single(ctx.Environments);
        Assert.Contains("[truncated by budget]", env.Content);
        Assert.True(env.Content.Length <= 50, $"content length {env.Content.Length} exceeds budget 50");
        Assert.Null(ctx.Overview);                          // Budget nach Umgebung erschöpft
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: FAIL — `Overview` still null; content not truncated.

- [ ] **Step 3: Add overview + budget**

In `WorkspaceContextCollector.cs`, change `CollectAsync` to build the overview and apply the budget:

```csharp
    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = workspace.RootPath;

        var environments = CollectEnvironments(root, changes);
        var usages = CollectUsages(root, changes);
        var overview = BuildOverview(root);

        var ctx = ApplyBudget(new ReviewContext(environments, usages, overview));
        return Task.FromResult(ctx);
    }
```

Add these members (place the overview/budget block after `EnumerateSourceFiles`):

```csharp
    // ---- Überblick --------------------------------------------------------
    private const int MaxTreeEntriesPerDir = 40;

    private string? BuildOverview(string root)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Directory tree (depth ≤ {options.MaxTreeDepth}):");
        AppendTree(sb, root, prefix: "", depth: 0);

        var readme = FindReadme(root);
        if (readme is not null)
        {
            try
            {
                var head = File.ReadLines(readme).Take(options.ReadmeMaxLines).ToList();
                if (head.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"README (first {options.ReadmeMaxLines} lines):");
                    foreach (var l in head) sb.AppendLine(l);
                }
            }
            catch { /* README unlesbar -> nur Baum */ }
        }

        var text = sb.ToString().TrimEnd();
        return text.Length == 0 ? null : text;
    }

    private void AppendTree(System.Text.StringBuilder sb, string dir, string prefix, int depth)
    {
        if (depth >= options.MaxTreeDepth) return;
        List<string> subdirs, files;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir)
                .Where(d => !ExcludedDirs.Contains(Path.GetFileName(d)))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).Take(MaxTreeEntriesPerDir).ToList();
            files = Directory.EnumerateFiles(dir)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal).Take(MaxTreeEntriesPerDir).ToList();
        }
        catch { return; }

        foreach (var d in subdirs)
        {
            sb.AppendLine($"{prefix}{Path.GetFileName(d)}/");
            AppendTree(sb, d, prefix + "  ", depth + 1);
        }
        foreach (var f in files)
            sb.AppendLine($"{prefix}{Path.GetFileName(f)}");
    }

    private static string? FindReadme(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root)
                .Where(f => Path.GetFileName(f).StartsWith("README", StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    // ---- Budget -----------------------------------------------------------
    private const string BudgetMarker = "\n… [truncated by budget]";

    // Füllt in Priorität Umgebung > Call-Sites > Überblick bis MaxChars; der erste überlaufende
    // Block wird markiert abgeschnitten, alles danach fällt weg. Deterministisch.
    private ReviewContext ApplyBudget(ReviewContext ctx)
    {
        int budget = options.MaxChars;

        var envs = new List<FileEnvironment>();
        foreach (var e in ctx.Environments)
        {
            if (budget <= 0) break;
            var (content, used, truncated) = Fit(e.Content, budget);
            envs.Add(e with { Content = content });
            budget -= used;
            if (truncated) { budget = 0; break; }
        }

        var usages = new List<SymbolUsage>();
        foreach (var u in ctx.Usages)
        {
            if (budget <= 0) break;
            var (snippet, used, truncated) = Fit(u.Snippet, budget);
            usages.Add(u with { Snippet = snippet });
            budget -= used;
            if (truncated) { budget = 0; break; }
        }

        string? overview = null;
        if (budget > 0 && ctx.Overview is not null)
            overview = Fit(ctx.Overview, budget).Text;

        return new ReviewContext(envs, usages, overview);
    }

    private static (string Text, int Used, bool Truncated) Fit(string text, int budget)
    {
        if (text.Length <= budget) return (text, text.Length, false);
        var keep = Math.Max(0, budget - BudgetMarker.Length);
        return (text[..keep] + BudgetMarker, budget, true);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter WorkspaceContextCollectorTests`
Expected: PASS (8 tests).

> Note: with `MaxChars = 50` and `BudgetMarker` length 24, `Fit` keeps 26 chars + marker = 50 — within budget. The assertion `Content.Length <= 50` holds.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Context/WorkspaceContextCollector.cs tests/Naudit.Tests/WorkspaceContextCollectorTests.cs
git commit -m "feat(context): Repo-Überblick (Baum + README-Kopf) + Zeichen-Budget mit Priorität"
```

---

### Task 5: PromptBuilder renders the context section

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs` (add tests)

**Interfaces:**
- Consumes: `ReviewContext`/`FileEnvironment`/`SymbolUsage` (Task 1).
- Produces: `PromptBuilder.Build(string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes, IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null)` — renders a "Repository context" section between diffs and findings; empty/`null` context renders nothing. `DefaultSystemPrompt` mentions the read-only context.

- [ ] **Step 1: Write the failing tests**

Append to `tests/Naudit.Tests/PromtBuilderTests.cs` (inside `PromptBuilderTests`):

```csharp
    [Fact]
    public void Build_rendersContextSection_whenPresent()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var context = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "class Foo { }", IsFullFile: true) },
            new[] { new SymbolUsage("DoThing", "src/Bar.cs", 42, "  DoThing();") },
            "Directory tree (depth ≤ 3):\nsrc/");

        var text = PromptBuilder.Build("SYS", request, changes, null, context)[1].Text!;

        Assert.Contains("# Repository context", text);
        Assert.Contains("## Surrounding code", text);
        Assert.Contains("src/Foo.cs (full file)", text);
        Assert.Contains("class Foo { }", text);
        Assert.Contains("## Usages of changed symbols", text);
        Assert.Contains("`DoThing` — src/Bar.cs:42", text);
        Assert.Contains("## Repository overview", text);
        Assert.Contains("Directory tree", text);
    }

    [Fact]
    public void Build_blockEnvironment_showsStartLine()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };
        var context = new ReviewContext(
            new[] { new FileEnvironment("src/Big.cs", 120, "void M() { }", IsFullFile: false) },
            Array.Empty<SymbolUsage>(), null);

        var text = PromptBuilder.Build("SYS", request, changes, null, context)[1].Text!;

        Assert.Contains("src/Big.cs (from line 120)", text);
    }

    [Fact]
    public void Build_withoutContext_rendersNoContextSection()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes)[1].Text!;

        Assert.DoesNotContain("# Repository context", text);
    }

    [Fact]
    public void Build_emptyContext_rendersNoContextSection()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ +1 @@") };

        var text = PromptBuilder.Build("SYS", request, changes, null, ReviewContext.Empty)[1].Text!;

        Assert.DoesNotContain("# Repository context", text);
    }

    [Fact]
    public void DefaultSystemPrompt_mentionsRepositoryContext()
    {
        Assert.Contains("Repository context", PromptBuilder.DefaultSystemPrompt);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`
Expected: FAIL — `Build` has no 5th parameter; `DefaultSystemPrompt` lacks the phrase.

- [ ] **Step 3: Extend `DefaultSystemPrompt` and `Build`**

In `src/Naudit.Core/Review/PromtBuilder.cs`, append one sentence to `DefaultSystemPrompt` — change the final line of the constant from:

```csharp
        "Use \"summary\" only for the one-line overview, never to carry findings.";
```

to:

```csharp
        "Use \"summary\" only for the one-line overview, never to carry findings. " +
        "A read-only \"Repository context\" section may follow the diff (surrounding code, usages, repository overview) - " +
        "use it to understand what the change does and how it fits, but report findings ONLY on the diff lines shown with line numbers.";
```

Change the `Build` signature and insert the context rendering between the diff loop and `AppendFindings`:

```csharp
    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Merge Request: {request.Title}");
        foreach (var change in changes)
        {
            sb.AppendLine();
            sb.AppendLine($"## File: {change.FilePath}");
            sb.AppendLine("```diff");
            AppendAnnotatedDiff(sb, change.Diff);
            sb.AppendLine("```");
        }

        AppendContext(sb, context);
        AppendFindings(sb, findings ?? []);

        return new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, sb.ToString()),
        };
    }
```

Add the `AppendContext` method (place it before `AppendFindings`):

```csharp
    // Repo-Kontext als read-only Grounding: umgebender Code, Call-Sites, Überblick. Leerer Kontext
    // rendert nichts (Prompt bleibt byte-identisch zum diff-only-Pfad).
    private static void AppendContext(StringBuilder sb, ReviewContext? context)
    {
        if (context is null)
            return;
        var hasAny = context.Environments.Count > 0 || context.Usages.Count > 0 || context.Overview is not null;
        if (!hasAny)
            return;

        sb.AppendLine();
        sb.AppendLine("# Repository context (read-only grounding from the checked-out repo)");
        sb.AppendLine("Use it to understand the change; report findings ONLY on the diff lines above.");

        if (context.Environments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Surrounding code");
            foreach (var e in context.Environments)
            {
                sb.AppendLine();
                var label = e.IsFullFile ? "full file" : $"from line {e.StartLine}";
                sb.AppendLine($"### {e.FilePath} ({label})");
                sb.AppendLine("```");
                sb.AppendLine(e.Content);
                sb.AppendLine("```");
            }
        }

        if (context.Usages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Usages of changed symbols");
            foreach (var u in context.Usages)
            {
                sb.AppendLine();
                sb.AppendLine($"### `{u.Symbol}` — {u.FilePath}:{u.Line}");
                sb.AppendLine("```");
                sb.AppendLine(u.Snippet);
                sb.AppendLine("```");
            }
        }

        if (context.Overview is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Repository overview");
            sb.AppendLine(context.Overview);
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`
Expected: PASS (all PromptBuilder tests, including the 5 new ones).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Review/PromtBuilder.cs tests/Naudit.Tests/PromtBuilderTests.cs
git commit -m "feat(context): PromptBuilder rendert Repository-Kontext-Sektion (leer ⇒ unverändert)"
```

---

### Task 6: ReviewService integration + DI registration

**Files:**
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Create: `tests/Naudit.Tests/Fakes/FakeContextCollector.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`
- Modify: `tests/Naudit.Tests/ReviewEndpointTests.cs`

**Interfaces:**
- Consumes: `IContextCollector`, `ReviewContext` (Task 1); `WorkspaceContextCollector` (Tasks 2–4); `PromptBuilder.Build(..., context)` (Task 5).
- Produces: `ReviewService` ctor gains a trailing `IContextCollector contextCollector` param; one shared checkout serves SAST **and** context; context is redacted and passed to `PromptBuilder.Build`. DI registers `IContextCollector → WorkspaceContextCollector(reviewOptions.Context)`.

- [ ] **Step 1: Create the fake**

Create `tests/Naudit.Tests/Fakes/FakeContextCollector.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

internal sealed class FakeContextCollector(ReviewContext? context = null, bool throws = false) : IContextCollector
{
    public bool Called { get; private set; }

    public Task<ReviewContext> CollectAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        Called = true;
        if (throws)
            throw new InvalidOperationException("collector boom");
        return Task.FromResult(context ?? ReviewContext.Empty);
    }
}
```

- [ ] **Step 2: Write the failing tests**

In `tests/Naudit.Tests/ReviewServiceTests.cs`, update the `CreateService` helper to inject the collector, replace the old `ReviewAsync_noAnalyzers_doesNotCheckout` test, and add four new tests.

Replace the `CreateService` helper with:

```csharp
    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null,
        IPromptRedactor? redactor = null,
        IContextCollector? contextCollector = null)
        => new(chat, git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector());
```

Replace the existing test `ReviewAsync_noAnalyzers_doesNotCheckout` with (context off ⇒ still no checkout):

```csharp
    [Fact]
    public async Task ReviewAsync_noAnalyzers_andContextDisabled_doesNotCheckout()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ws = new FakeWorkspaceProvider();
        var options = new ReviewOptions();
        options.Context.Enabled = false;
        var service = CreateService(chat, git, options, analyzers: null, workspace: ws);

        await service.ReviewAsync(Request);

        Assert.False(ws.CheckoutCalled);
    }
```

Add these four tests to the class:

```csharp
    [Fact]
    public async Task ReviewAsync_contextEnabled_noAnalyzers_checksOut_andCollects()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ws = new FakeWorkspaceProvider();
        var collector = new FakeContextCollector();   // Context.Enabled default true
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers: null, workspace: ws, contextCollector: collector);

        await service.ReviewAsync(Request);

        Assert.True(ws.CheckoutCalled);
        Assert.True(collector.Called);
    }

    [Fact]
    public async Task ReviewAsync_groundsContext_inPrompt()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ctx = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "class Foo { }", IsFullFile: true) },
            Array.Empty<SymbolUsage>(), null);
        var collector = new FakeContextCollector(ctx);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            contextCollector: collector);

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.Contains("# Repository context", userText);
        Assert.Contains("class Foo { }", userText);
    }

    [Fact]
    public async Task ReviewAsync_redactsContext_beforePrompt()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var ctx = new ReviewContext(
            new[] { new FileEnvironment("src/Foo.cs", 1, "var k = \"SECRET\";", IsFullFile: true) },
            Array.Empty<SymbolUsage>(), null);
        var redactor = new FakePromptRedactor("SECRET");
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            redactor: redactor, contextCollector: new FakeContextCollector(ctx));

        await service.ReviewAsync(Request);

        var userText = chat.LastMessages![1].Text!;
        Assert.DoesNotContain("SECRET", userText);   // Kontext läuft durch den Redactor
        Assert.Contains("«red»", userText);
    }

    [Fact]
    public async Task ReviewAsync_contextCollectorThrows_stillReviews_diffOnly()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@")]);
        var collector = new FakeContextCollector(throws: true);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            contextCollector: collector);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
        Assert.Equal(1, git.PostCallCount);
        Assert.DoesNotContain("# Repository context", chat.LastMessages![1].Text!);
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: FAIL — compile error (`ReviewService` ctor has no `IContextCollector` param).

- [ ] **Step 4: Wire `IContextCollector` into `ReviewService`**

In `src/Naudit.Core/Review/ReviewService.cs`:

(a) Add the ctor parameter:

```csharp
public sealed class ReviewService(
    IChatClient chatClient,
    IGitPlatform gitPlatform,
    ReviewOptions options,
    IWorkspaceProvider workspaceProvider,
    IEnumerable<ISastAnalyzer> analyzers,
    IFindingReducer findingReducer,
    IPromptRedactor redactor,
    IContextCollector contextCollector)
```

(b) In `ReviewAsync`, replace the findings-collection + redaction + prompt-build block. Change:

```csharp
        // SAST/SCA-Grounding vor dem Prompt-Aufbau einsammeln (leer, wenn Feature aus).
        var findings = await CollectFindingsAsync(request, changes, ct);

        // Redaction: ...
        var redChanges = new List<CodeChange>(changes.Count);
        foreach (var c in changes)
            redChanges.Add(c with { Diff = await redactor.RedactAsync(c.Diff, ct) });

        var redFindings = new List<ScanFinding>(findings.Count);
        foreach (var f in findings)
            redFindings.Add(f with { Message = await redactor.RedactAsync(f.Message, ct) });

        var redRequest = request with { Title = await redactor.RedactAsync(request.Title, ct) };

        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings);
```

to:

```csharp
        // Grounding aus EINEM geteilten Checkout: SAST-Funde + Kontext (je leer, wenn Feature aus).
        var (findings, context) = await GatherGroundingAsync(request, changes, ct);

        // Redaction: Secrets/IPs/E-Mails maskieren, BEVOR irgendetwas das LLM erreicht.
        var redChanges = new List<CodeChange>(changes.Count);
        foreach (var c in changes)
            redChanges.Add(c with { Diff = await redactor.RedactAsync(c.Diff, ct) });

        var redFindings = new List<ScanFinding>(findings.Count);
        foreach (var f in findings)
            redFindings.Add(f with { Message = await redactor.RedactAsync(f.Message, ct) });

        var redRequest = request with { Title = await redactor.RedactAsync(request.Title, ct) };
        var redContext = await RedactContextAsync(context, ct);

        var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings, redContext);
```

(c) Replace the whole `CollectFindingsAsync` method with the shared-checkout orchestration plus the two collectors and the context redactor. Delete `CollectFindingsAsync` and add:

```csharp
    // Ein Checkout für beide Grounding-Quellen: SAST-Analyzer UND Kontext-Sammler. Checkout nur,
    // wenn mindestens eine Quelle aktiv ist. Checkout-Fehler ⇒ diff-only (Infrastructure hat geloggt).
    private async Task<(IReadOnlyList<ScanFinding> Findings, ReviewContext Context)> GatherGroundingAsync(
        ReviewRequest request, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        var needCheckout = _analyzers.Count > 0 || options.Context.Enabled;
        if (!needCheckout)
            return ([], ReviewContext.Empty);

        IReviewWorkspace workspace;
        try
        {
            workspace = await workspaceProvider.CheckoutAsync(request, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return ([], ReviewContext.Empty); // Checkout fehlgeschlagen → diff-only
        }

        await using (workspace)
        {
            var findings = _analyzers.Count > 0
                ? await RunAnalyzersAsync(workspace, changes, ct)
                : Array.Empty<ScanFinding>();

            var context = options.Context.Enabled
                ? await SafeCollectContextAsync(workspace, changes, ct)
                : ReviewContext.Empty;

            return (findings, context);
        }
    }

    private async Task<IReadOnlyList<ScanFinding>> RunAnalyzersAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        var results = await Task.WhenAll(_analyzers.Select(a => SafeAnalyzeAsync(a, workspace, changes, ct)));

        var changed = new HashSet<string>(changes.Select(c => c.FilePath));
        var annotated = results
            .SelectMany(r => r)
            .Select(f => f.FilePath is not null && changed.Contains(f.FilePath) ? f with { InDiff = true } : f)
            .ToList();

        return await findingReducer.ReduceAsync(annotated, changes, ct);
    }

    // Ein Sammler-Fehler kippt den Review nicht: degradiert auf leeren Kontext (diff-only-Prompt).
    private async Task<ReviewContext> SafeCollectContextAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct)
    {
        try { return await contextCollector.CollectAsync(workspace, changes, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { return ReviewContext.Empty; }
    }

    // Kontext läuft — wie Diff/Finding/Titel — vor dem Prompt durch den Redactor.
    private async Task<ReviewContext> RedactContextAsync(ReviewContext ctx, CancellationToken ct)
    {
        if (ctx.Environments.Count == 0 && ctx.Usages.Count == 0 && ctx.Overview is null)
            return ReviewContext.Empty;

        var envs = new List<FileEnvironment>(ctx.Environments.Count);
        foreach (var e in ctx.Environments)
            envs.Add(e with { Content = await redactor.RedactAsync(e.Content, ct) });

        var usages = new List<SymbolUsage>(ctx.Usages.Count);
        foreach (var u in ctx.Usages)
            usages.Add(u with { Snippet = await redactor.RedactAsync(u.Snippet, ct) });

        var overview = ctx.Overview is null ? null : await redactor.RedactAsync(ctx.Overview, ct);
        return new ReviewContext(envs, usages, overview);
    }
```

> `SafeAnalyzeAsync`, `IsBlocking`, `ComposeSummary`, and the rest of the class stay unchanged.

- [ ] **Step 5: Register the collector in DI**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, add the `using` and the registration. After the line `services.AddScoped<IWorkspaceProvider, GitWorkspaceProvider>();` add:

```csharp
        // Kontext-Anreicherung: aus demselben Checkout wie SAST, gesteuert über reviewOptions.Context.
        services.AddScoped<IContextCollector>(_ => new WorkspaceContextCollector(reviewOptions.Context));
```

Add the namespace import at the top with the other `Naudit.Infrastructure.*` usings:

```csharp
using Naudit.Infrastructure.Context;
```

- [ ] **Step 6: Keep the `/review` endpoint test diff-only (fast)**

In `tests/Naudit.Tests/ReviewEndpointTests.cs`, the `Review_withValidToken_runsReview_andReturnsVerdict` test now would trigger a real checkout for context (context defaults on) and shell out to `git`. Disable context for this test so it stays a pure diff-only unit. Add one `UseSetting` line inside its `WithWebHostBuilder`, next to the other `UseSetting` calls:

```csharp
                b.UseSetting("Naudit:Git:Platform", "GitLab");
                b.UseSetting("Naudit:GitLab:WebhookSecret", "test-secret");
                b.UseSetting("Naudit:Review:Context:Enabled", "false");
```

- [ ] **Step 7: Run the affected suites to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ReviewServiceTests|ReviewEndpointTests|SastWiringTests"`
Expected: PASS (all three classes green — collector resolves through DI, endpoint stays diff-only).

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Core/Review/ReviewService.cs src/Naudit.Infrastructure/DependencyInjection.cs tests/Naudit.Tests/Fakes/FakeContextCollector.cs tests/Naudit.Tests/ReviewServiceTests.cs tests/Naudit.Tests/ReviewEndpointTests.cs
git commit -m "feat(context): ReviewService — geteilter Checkout, Kontext redigiert in den Prompt; DI-Registrierung"
```

---

### Task 7: Documentation

**Files:**
- Create: `docs/review-context.md`
- Modify: `docs/configuration.md` (add `Naudit:Review:Context:*` rows)
- Modify: `CLAUDE.md` (extension-points list + request-flow note)

**Interfaces:**
- Consumes: the shipped feature (Tasks 1–6). No code.

- [ ] **Step 1: Write `docs/review-context.md`**

Create `docs/review-context.md`:

```markdown
# Review context enrichment

Naudit gives the reviewing LLM more than the raw diff: it cuts **surrounding code**,
**call-sites of changed symbols**, and a **repository overview** from the shallow
checkout it already makes (the same one the SAST analyzers use) and appends them to the
prompt as a read-only "Repository context" section. Nothing is installed in the target
repository — the context is derived per review and thrown away with the checkout.

## What it collects

1. **Surrounding code** — for each changed file: the whole file if it is at most
   `FullFileMaxLines` lines; otherwise the enclosing block around each changed hunk
   (indentation heuristic, with a `BlockPadLines` fallback window).
2. **Usages of changed symbols** — symbol names declared on added (`+`) diff lines
   (functions, types, C-family signatures) searched across the checkout (excluding the
   declaring files and vendor/build directories), up to `MaxUsagesPerSymbol` call-sites
   with `UsageSnippetLines` of surrounding context each.
3. **Repository overview** — a directory tree (depth ≤ `MaxTreeDepth`) plus the first
   `ReadmeMaxLines` of a root `README.*`.

The section is assembled in priority order **surrounding code → usages → overview** and
truncated at `MaxChars` (the overrun point is marked `… [truncated by budget]`).

## Configuration

All keys live under `Naudit:Review:Context` (defaults in parentheses):

| Key | Meaning |
| --- | --- |
| `Enabled` (`true`) | Build the context section at all. `false` ⇒ today's diff-only prompt. |
| `MaxChars` (`40000`) | Character budget for the whole context section. |
| `FullFileMaxLines` (`400`) | File at most this many lines ⇒ whole file; larger ⇒ block excerpts. |
| `BlockPadLines` (`30`) | ± fallback window around a hunk anchor when the block heuristic is too tight. |
| `UsageSnippetLines` (`3`) | ± lines around each call-site. |
| `MaxUsagesPerSymbol` (`5`) | Cap on call-sites per symbol. |
| `MaxTreeDepth` (`3`) | Directory-tree depth in the overview. |
| `ReadmeMaxLines` (`50`) | README head length in the overview. |

## Cost note

Because context needs the checkout, enabling it means **one shallow clone per review**
even when SAST is off. Set `Naudit:Review:Context:Enabled=false` to restore the exact
diff-only behaviour and prompt.

## Design & limits

The extraction is deliberately language-agnostic (regex + indentation), tuned for
precision over recall: a missed symbol is fine, prompt spam is not. Everything collected
passes through the prompt redactor before reaching the LLM, exactly like the diff,
findings, and title. Precise parsers (Roslyn/tree-sitter) and a cached architecture
"repo map" are possible later stages behind the same `IContextCollector` seam. See
`docs/superpowers/specs/2026-07-07-review-context-enrichment-design.md`.
```

- [ ] **Step 2: Add the config rows to `docs/configuration.md`**

In `docs/configuration.md`, in the `## Keys` table, after the `Naudit:Review:Gate:MinConfidence` row, add:

```markdown
| `Naudit:Review:Context:Enabled` | Enrich the prompt with surrounding code / usages / repo overview from the checkout — **default `true`** (see [Review context](review-context.md)) |
| `Naudit:Review:Context:MaxChars` | Character budget for the context section (default `40000`) |
| `Naudit:Review:Context:FullFileMaxLines` | Changed file ≤ this ⇒ whole file in context; larger ⇒ block excerpts (default `400`) |
| `Naudit:Review:Context:MaxUsagesPerSymbol` | Max call-sites shown per changed symbol (default `5`) |
```

- [ ] **Step 3: Update `CLAUDE.md`**

In `CLAUDE.md`, in the `### Request flow` paragraph, change the collection step. Find:

```
which: `IGitPlatform.GetChangesAsync` → (optional SAST/SCA grounding) →
```

and replace with:

```
which: `IGitPlatform.GetChangesAsync` → (optional SAST/SCA grounding **and** repo-context enrichment, one shared checkout) →
```

Then in the `### Extension points` list, after the **New prompt redactor** bullet, add:

```markdown
- **Repo-context collector:** `IContextCollector` (Core `Abstractions`) cuts surrounding
  code, call-sites of changed symbols, and a repo overview from the shared workspace
  checkout; the default `WorkspaceContextCollector`
  (`src/Naudit.Infrastructure/Context/`) is language-agnostic (regex + indentation).
  Collected context is redacted like the diff and rendered by `PromptBuilder` as a
  read-only "Repository context" section. On by default; `Naudit:Review:Context:Enabled=false`
  restores the diff-only prompt. Seam for a future Roslyn/tree-sitter or cached "repo map"
  collector — just another impl + registration. See `docs/review-context.md`.
```

- [ ] **Step 4: Verify the full suite is green**

Run: `dotnet build Naudit.slnx && dotnet test Naudit.slnx`
Expected: build succeeds; all tests pass (existing suite + the new context tests).

- [ ] **Step 5: Commit**

```bash
git add docs/review-context.md docs/configuration.md CLAUDE.md
git commit -m "docs(context): review-context.md + Config-Keys + CLAUDE.md-Naht"
```

---

## Self-Review notes (for the implementer)

- **Spec coverage:** environment/usages/overview (Tasks 2–4), budget + priority (Task 4),
  language-agnostic heuristics (Tasks 2–3), all-provider single-shot prompt (Task 5),
  shared checkout + fail-open + redaction + toggle (Task 6), config + docs (Tasks 1, 7).
- **Behaviour change to watch:** context defaults **on**, so a review with SAST off now
  checks out once for context. Two existing tests are updated for this in Task 6
  (`ReviewServiceTests` checkout test; `ReviewEndpointTests` gets `Context:Enabled=false`).
- **Determinism:** `EnumerateSourceFiles` and the tree walk sort by name; budgeting is a
  single deterministic pass.
- **Core rule:** the only new Core types are the interface, the `ReviewContext` records,
  and the options — all MEAI-free. The implementation and all `System.IO`/regex live in
  Infrastructure.
```
