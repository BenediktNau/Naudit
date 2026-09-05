# Altlasten getrennt behandeln — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Werkzeugbefunde, die nicht aus dem Diff stammen, verdrängen keine Diff-Befunde mehr aus dem Prompt und werden stattdessen deterministisch aggregiert — als Baseline-Sektion im Prompt und als eigener MR/PR-Kommentar.

**Architecture:** `ReviewService` markiert jeden Fund zeilengenau (`InDiff`) und dateiweise (`InChangedFile`). Der `DeterministicFindingReducer` sortiert dreistufig und vergibt getrennte Kontingente je Kategorie; er liefert ab jetzt ein `FindingReduction` (Prompt-Auswahl **plus** alle Altlasten). Eine neue, rein deterministische Verdichtung `PreExistingSummary` weist High/Critical-Altlasten einzeln mit Datei:Zeile aus und fasst den Rest nach Regel zusammen. Dieselbe Verdichtung speist zwei Konsumenten: eine Prompt-Sektion (englisch, wie der übrige Prompt) und einen eigenständigen MR/PR-Kommentar (deutsch, wie die Summary) über die neue Seam-Methode `IGitPlatform.PostNoteAsync`.

**Tech Stack:** .NET 10, xUnit, Microsoft.Extensions.AI. Solution ist `Naudit.slnx` (**nicht** `.sln`).

**Spec:** `/home/bnau/naudit-laeufe-2026-09-04/handoff-altlasten.md` — dazu die in der Planungssitzung getroffenen Abweichungen, festgehalten unter „Entscheidungen" unten.

## Global Constraints

- **Core-Regel:** `Naudit.Core` hängt ausschließlich an `Microsoft.Extensions.AI.Abstractions`. Kein Provider-SDK, keine Plattform-SDKs, **kein Zugriff auf `SastOptions`** (das lebt in Infrastructure). Reducer bleibt in Infrastructure.
- **Solution:** `dotnet build Naudit.slnx`, `dotnet test Naudit.slnx`. `dotnet test Naudit.sln` scheitert mit MSB1009.
- **Volle Suite immer mit** `DOTNET_USE_POLLING_FILE_WATCHER=1` laufen lassen — sonst flakt sie am inotify-Limit (2–7 zufällige Endpoint-Fehler).
- **Code-Kommentare auf Deutsch**, Doku (`docs/`, README) auf Englisch.
- **Das Verdict bleibt unberührt.** Das severity-bewusste Gate rechnet weiterhin ausschließlich über die LLM-Findings. Altlasten blocken nie — bestehende Gate-Tests müssen unverändert grün bleiben.
- **Fail-open wie überall im Grounding:** ein Fehler beim Aggregieren oder beim Posten des Altlasten-Kommentars darf das Review nie kippen.
- TDD: rot → grün → ein Commit pro Task.

## Entscheidungen (weichen bewusst vom Handoff ab)

Gemessen am zwischengespeicherten cal.com-Checkout mit dem vollen Opengrep-Regelbaum: **2.817 Befunde verteilen sich auf nur 48 distinkte Regeln**, die Top-5-Regeln decken 91,6 % (allein `i18next-key-format` 1.763× = 62,6 %). Die komplette ERROR/High-Ebene sind **21 Befunde in 7 Regeln**.

Daraus folgen drei Abweichungen vom Handoff-Text:

1. **Gruppierungseinheit ist die Regel, nicht der Einzelbefund.** 2.817 Zeilen schrumpfen auf 48; ein Deckel von 30 Regeln deckt 99,1 %.
2. **Severity-abhängige Verdichtung statt uniformer Top-N.** High/Critical-Altlasten werden **einzeln mit Datei:Zeile** ausgewiesen (bei cal.com 21 Zeilen — passt problemlos), erst Medium/Low/Info werden nach Regel verdichtet. Sonst verschwände ein echtes Sicherheitsproblem außerhalb geänderter Dateien im Aggregat.
3. **Dreistufige Sortierung statt binär.** Stufe 0 = Fund auf kommentierbarer Diff-Zeile, Stufe 1 = Fund in geänderter Datei außerhalb der Hunks, Stufe 2 = Fund in unberührter Datei. Stufe 0 und 1 kommen einzeln in den Prompt (getrennte Kontingente), Stufe 2 ausschließlich aggregiert.

Weitere Festlegungen:

- Der Altlasten-Kommentar wird **nur beim ersten Review pro PR** gepostet (Altlasten ändern sich zwischen zwei Pushes nicht).
- Konfiguration wird **nach Besitzer getrennt**: die Reducer-Kappung bleibt bei ihrem Konsumenten in `SastOptions`, die Aggregations-/Kommentar-Optionen kommen als `ReviewOptions.PreExisting` (Core) — sonst müsste die DI Werte zwischen zwei Options-Objekten umkopieren.
- `InDiff` gilt für **kommentierbare** Hunk-Zeilen (hinzugefügte **und** Kontextzeilen) — genau das, was `DiffParser.Parse` liefert und was das Modell verankern kann. Funde **ohne** Zeilennummer in einer geänderten Datei fallen auf die Datei-Regel zurück, sonst würde jeder SCA-Fund einer geänderten Lockfile fälschlich zur Altlast.

**Nicht Teil dieses Plans** (bewusst verworfen bzw. vertagt):

- Datei-ebene Kommentare (`subject_type: "file"` / `position_type: "file"`) für Stufe-1-Befunde — eigener PR, ändert `InlineComment.NewLine` auf nullable und zieht beide Plattform-Clients, Audit und `@naudit fp`-Zuordnung mit.
- GitHub-Check-Run-Annotations für Stufe-2-Befunde — GitHub-only und nur im App-Modus, bricht die Plattform-Symmetrie.

## File Structure

**Core (neu):**
- `src/Naudit.Core/Models/FindingReduction.cs` — Rückgabetyp des Reducers: Prompt-Auswahl + alle Altlasten.
- `src/Naudit.Core/Review/PreExistingSummary.cs` — der deterministische Verdichtungs-Algorithmus und sein Ergebnistyp. Eine Verantwortung: aus Altlast-Funden eine gekappte, severity-bewusste Darstellung machen. Kein I/O.
- `src/Naudit.Core/Review/PreExistingReport.cs` — rendert eine `PreExistingSummary` als deutschen Markdown-Kommentar. Getrennt von `PreExistingSummary`, weil die Prompt-Sektion (englisch) dieselbe Verdichtung anders rendert.

**Core (geändert):**
- `src/Naudit.Core/Models/ScanFinding.cs` — `InChangedFile` dazu.
- `src/Naudit.Core/Abstractions/IFindingReducer.cs` — Rückgabetyp `FindingReduction`.
- `src/Naudit.Core/Abstractions/IGitPlatform.cs` — `PostNoteAsync`.
- `src/Naudit.Core/Review/ReviewOptions.cs` — `PreExistingOptions`.
- `src/Naudit.Core/Review/ReviewService.cs` — zeilengenaue Markierung, `FindingReduction` durchreichen, Kommentar posten.
- `src/Naudit.Core/Review/PromtBuilder.cs` — Baseline-Sektion.

**Infrastructure (geändert):**
- `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs` — dreistufige Sortierung, getrennte Kontingente.
- `src/Naudit.Infrastructure/Sast/SastOptions.cs` — `MaxPreExistingPerGroup`.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`, `.../GitHub/GitHubPlatform.cs` — `PostNoteAsync`.
- `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`, `src/Naudit.Infrastructure/DependencyInjection.cs`.

**Tests (neu):** `PreExistingSummaryTests.cs`, `PreExistingReportTests.cs`.
**Tests (geändert):** `DeterministicFindingReducerTests.cs`, `ReviewServiceTests.cs`, `PromtBuilderTests.cs`, `GitLabPlatformTests.cs`, `GitHubPlatformTests.cs`, `Fakes/FakeFindingReducer.cs`, `Fakes/FakeGitPlatform.cs`, `ReviewAuditSinkTests.cs`, `ReviewGuidelinesWiringTests.cs`.
**Tools (geändert):** `tools/Naudit.Benchmark/CapturingGitPlatform.cs` — vierte `IGitPlatform`-Implementierung, muss mitwachsen.

**Doku:** `docs/sast-grounding.md`, `docs/configuration.md`.

---

### Task 1: Zeilengenaue Fund-Markierung

Heute markiert `ReviewService.RunAnalyzersAsync` einen Fund als `InDiff`, sobald seine **Datei** im MR geändert wurde. Bei cal.com traf das auf 92 Befunde zu, von denen nur ~21 überhaupt auf einer kommentierbaren Zeile lagen. Diese Aufgabe macht die Markierung zeilengenau und führt die zweite Stufe (`InChangedFile`) ein.

**Files:**
- Modify: `src/Naudit.Core/Models/ScanFinding.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs` (Zeilen 41–46, 99, 177–223)
- Test: `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `DiffParser.Parse(IReadOnlyList<CodeChange>) → IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>>` (bestehend).
- Produces: `ScanFinding.InChangedFile` (bool, init-only). Invariante für Task 2 und 4: `InDiff == true` impliziert `InChangedFile == true`.

- [ ] **Step 1: Write the failing test**

In `tests/Naudit.Tests/ReviewServiceTests.cs` ans Ende der Klasse:

```csharp
    [Fact]
    public async Task ReviewAsync_marksFindingOutsideHunk_asPreExisting_evenInChangedFile()
    {
        // Diff berührt nur Zeile 10; der Fund sitzt auf Zeile 500 derselben Datei.
        // Frueher: InDiff=true (Datei-Regel) und damit faelschlich prompt-priorisiert.
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -10,1 +10,1 @@\n+touched")]);
        var analyzer = new FakeSastAnalyzer("opengrep",
        [
            new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "im-hunk", "R-IN", "a.cs", 10),
            new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "ausserhalb", "R-OUT", "a.cs", 500),
            new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "andere-datei", "R-FAR", "b.cs", 3),
        ]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers: [analyzer]);

        await service.ReviewAsync(Request);

        var prompt = chat.LastMessages![1].Text;
        Assert.Contains("[in diff] opengrep · R-IN", prompt);
        Assert.Contains("[pre-existing] opengrep · R-OUT", prompt);
        Assert.Contains("[pre-existing] opengrep · R-FAR", prompt);
    }

    [Fact]
    public async Task ReviewAsync_keepsFileLevelFinding_inDiff_whenLineIsUnknown()
    {
        // SCA-Funde (Trivy/OSV auf einer Lockfile) tragen oft keine Zeile. Sie duerfen nicht
        // zur Altlast werden, nur weil die Zeilennummer fehlt — sonst faellt jeder Dependency-Fund
        // einer im MR geaenderten Lockfile hinten runter.
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("package-lock.json", "@@ -1,1 +1,1 @@\n+dep")]);
        var analyzer = new FakeSastAnalyzer("trivy",
        [
            new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.High, "CVE", "CVE-1", "package-lock.json"),
        ]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" }, analyzers: [analyzer]);

        await service.ReviewAsync(Request);

        Assert.Contains("[in diff] trivy · CVE-1", chat.LastMessages![1].Text);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~ReviewAsync_marksFindingOutsideHunk|FullyQualifiedName~ReviewAsync_keepsFileLevelFinding"`

Expected: FAIL — der erste Test findet `[in diff] opengrep · R-OUT` statt `[pre-existing]`.

- [ ] **Step 3: Write minimal implementation**

In `src/Naudit.Core/Models/ScanFinding.cs`, unter `InDiff`:

```csharp
    /// <summary>Vom Orchestrator gesetzt: liegt der Fund auf einer kommentierbaren Diff-Zeile
    /// (hinzugefügt oder Kontext im Hunk)? Nur solche Funde kann das Modell verankern.</summary>
    public bool InDiff { get; init; }

    /// <summary>Vom Orchestrator gesetzt: liegt der Fund in einer im MR geänderten Datei — auch
    /// außerhalb der Hunks? <c>InDiff</c> impliziert immer <c>InChangedFile</c>.</summary>
    public bool InChangedFile { get; init; }
```

In `src/Naudit.Core/Review/ReviewService.cs`: die kommentierbaren Zeilen **einmal** nach `GetChangesAsync` bestimmen und beiden Verwendungsstellen geben. Zeile 41–46 wird zu:

```csharp
        var changes = await gitPlatform.GetChangesAsync(request, ct);
        if (changes.Count == 0)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve);

        // Einmal parsen, zweimal gebraucht: fuer die zeilengenaue Fund-Markierung (Grounding)
        // und weiter unten fuer die Verankerung der Modell-Kommentare.
        var commentable = DiffParser.Parse(changes);

        // Grounding aus EINEM geteilten Checkout: SAST-Funde + Kontext + Architektur-Profil (je leer/null, wenn Feature aus).
        var (findings, context, guidelines) = await GatherGroundingAsync(request, changes, commentable, ct);
```

Die spätere Zeile 99 (`var commentable = DiffParser.Parse(changes);`) ersatzlos löschen — die Variable existiert jetzt schon.

Signaturen durchreichen:

```csharp
    private async Task<(IReadOnlyList<ScanFinding> Findings, ReviewContext Context, string? Guidelines)> GatherGroundingAsync(
        ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>> commentable, CancellationToken ct)
```

und darin der Aufruf `await RunAnalyzersAsync(workspace, changes, commentable, ct)`.

`RunAnalyzersAsync` ersetzen:

```csharp
    private async Task<IReadOnlyList<ScanFinding>> RunAnalyzersAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>> commentable, CancellationToken ct)
    {
        var results = await Task.WhenAll(_analyzers.Select(a => SafeAnalyzeAsync(a, workspace, changes, ct)));

        var changed = new HashSet<string>(changes.Select(c => c.FilePath));
        var annotated = results
            .SelectMany(r => r)
            .Select(f => Annotate(f, changed, commentable))
            .ToList();

        return await findingReducer.ReduceAsync(annotated, changes, ct);
    }

    // Zweistufige Markierung. InDiff meint zeilengenau "auf einer kommentierbaren Diff-Zeile" —
    // nur dort kann das Modell ueberhaupt einen Kommentar verankern. Ein Fund OHNE Zeilennummer
    // (typisch SCA auf einer Lockfile) faellt auf die Datei-Regel zurueck, sonst waere jeder
    // Dependency-Fund einer im MR geaenderten Lockfile faelschlich eine Altlast.
    private static ScanFinding Annotate(
        ScanFinding f, HashSet<string> changed,
        IReadOnlyDictionary<string, IReadOnlyDictionary<int, int?>> commentable)
    {
        if (f.FilePath is null || !changed.Contains(f.FilePath))
            return f;

        var inDiff = f.Line is not int line
            || (commentable.TryGetValue(f.FilePath, out var lines) && lines.ContainsKey(line));

        return f with { InChangedFile = true, InDiff = inDiff };
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests`
Expected: PASS (alle, auch die bestehenden).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Core/Models/ScanFinding.cs src/Naudit.Core/Review/ReviewService.cs tests/Naudit.Tests/ReviewServiceTests.cs
git commit -m "feat(grounding): Werkzeugfunde zeilengenau statt dateiweise als InDiff markieren

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 2: Reducer — dreistufige Sortierung und getrennte Kontingente

Heute sortiert der Reducer Severity↓ dann InDiff↓ und kappt auf `MaxFindingsPerGroup` **je Kategorie**. Bei cal.com belegten damit 18 vorbestehende High-Befunde die Plätze 1–18, sodass für 92 Diff-Datei-Befunde noch 2 Plätze blieben. Diese Aufgabe dreht die Priorität um und gibt Altlasten ein eigenes, kleines Kontingent — und liefert die vollständige Altlast-Menge für die Aggregation in Task 3 mit zurück.

**Files:**
- Create: `src/Naudit.Core/Models/FindingReduction.cs`
- Modify: `src/Naudit.Core/Abstractions/IFindingReducer.cs`
- Modify: `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs`
- Modify: `src/Naudit.Infrastructure/Sast/SastOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs:292`
- Modify: `src/Naudit.Core/Review/ReviewService.cs` (Aufrufstelle in `RunAnalyzersAsync` / `GatherGroundingAsync`)
- Modify: `tests/Naudit.Tests/Fakes/FakeFindingReducer.cs`
- Test: `tests/Naudit.Tests/DeterministicFindingReducerTests.cs`

**Interfaces:**
- Consumes: `ScanFinding.InDiff` / `.InChangedFile` aus Task 1.
- Produces:
  - `FindingReduction(IReadOnlyList<ScanFinding> Selected, IReadOnlyList<ScanFinding> PreExisting)` mit `FindingReduction.Empty`.
  - `IFindingReducer.ReduceAsync(...) → Task<FindingReduction>`.
  - `SastOptions.MaxPreExistingPerGroup` (int, Default 5).
  - `DeterministicFindingReducer(int maxFindingsPerGroup = 20, int maxPreExistingPerGroup = 5)`.
  - Semantik für Task 3/4: `Selected` = Stufe 0 (bis `maxFindingsPerGroup` je Kategorie) **plus** Stufe 1 (bis `maxPreExistingPerGroup` je Kategorie); Stufe 2 kommt nie in `Selected`. `PreExisting` = **alle** dedupliziert-nicht-`InDiff`-Funde (Stufe 1 **und** 2), ungekappt. Die Stufe-1-Funde stehen also bewusst in beiden Listen: einzeln im Prompt und zusätzlich im Altlasten-Bericht.

- [ ] **Step 1: Write the failing test**

In `tests/Naudit.Tests/DeterministicFindingReducerTests.cs` — die bestehende Hilfsmethode ergänzen und die Abnahme-Tests dazu:

```csharp
    private static ScanFinding Tiered(string file, int line, string rule, FindingSeverity sev,
        bool inDiff = false, bool inChangedFile = false)
        => new("opengrep", FindingCategory.Sast, sev, "msg", rule, file, line)
        { InDiff = inDiff, InChangedFile = inChangedFile || inDiff };

    [Fact]
    public async Task Reduce_keepsAllDiffFindings_evenWhenOutrankedBySeverity()
    {
        // Abnahme-Kriterium: 18 High-Altlasten + 21 Medium-Diff-Befunde
        // => alle 21 Diff-Befunde ueberleben, Altlasten fuellen nur ihr eigenes Kontingent.
        // Diff-Kontingent bewusst 25 (nicht der Produktions-Default 20): geprueft wird, dass
        // ALTLASTEN keine Diff-Plaetze wegnehmen — nicht, wie gross das Diff-Kontingent ist.
        // Mit 20 wuerde der Test am eigenen Deckel scheitern und die falsche Sache messen.
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 25, maxPreExistingPerGroup: 5);
        var input = Enumerable.Range(0, 18)
            .Select(i => Tiered($"alt{i:D2}.cs", i, $"OLD{i:D2}", FindingSeverity.High))
            .Concat(Enumerable.Range(0, 21)
                .Select(i => Tiered($"neu{i:D2}.cs", i, $"NEW{i:D2}", FindingSeverity.Medium, inDiff: true)))
            .ToList();

        var result = await reducer.ReduceAsync(input, []);

        var diffRules = result.Selected.Where(f => f.InDiff).Select(f => f.RuleId).ToList();
        Assert.Equal(21, diffRules.Count);
        Assert.All(Enumerable.Range(0, 21), i => Assert.Contains($"NEW{i:D2}", diffRules));
    }

    [Fact]
    public async Task Reduce_capsPreExistingSeparately_andNeverTakesDiffSlots()
    {
        // Diff-Kontingent 25 aus demselben Grund wie oben: 21 Diff-Befunde muessen alle passen,
        // damit die Aussage ueber das getrennte Altlasten-Kontingent ueberhaupt pruefbar ist.
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 25, maxPreExistingPerGroup: 5);
        var input = Enumerable.Range(0, 18)
            .Select(i => Tiered($"alt{i:D2}.cs", i, $"OLD{i:D2}", FindingSeverity.High, inChangedFile: true))
            .Concat(Enumerable.Range(0, 21)
                .Select(i => Tiered($"neu{i:D2}.cs", i, $"NEW{i:D2}", FindingSeverity.Medium, inDiff: true)))
            .ToList();

        var result = await reducer.ReduceAsync(input, []);

        Assert.Equal(5, result.Selected.Count(f => !f.InDiff));
        Assert.Equal(26, result.Selected.Count);
    }

    [Fact]
    public async Task Reduce_sortsDiffFindingsFirst_thenSeverity()
    {
        var reducer = new DeterministicFindingReducer();
        var critPreExisting = Tiered("b.cs", 2, "R-CRIT", FindingSeverity.Critical);
        var lowInDiff = Tiered("c.cs", 3, "R-LOW", FindingSeverity.Low, inDiff: true);

        var result = await reducer.ReduceAsync(new[] { critPreExisting, lowInDiff }, []);

        Assert.Equal("R-LOW", result.Selected[0].RuleId);   // InDiff schlaegt Severity
        Assert.Equal("R-CRIT", result.Selected[1].RuleId);
    }

    [Fact]
    public async Task Reduce_excludesUntouchedFileFindings_fromSelected_butKeepsThemInPreExisting()
    {
        var reducer = new DeterministicFindingReducer(maxFindingsPerGroup: 20, maxPreExistingPerGroup: 5);
        var input = new[]
        {
            Tiered("in-hunk.cs", 1, "R-DIFF", FindingSeverity.Low, inDiff: true),
            Tiered("geaendert.cs", 900, "R-FILE", FindingSeverity.Low, inChangedFile: true),
            Tiered("fremd.cs", 5, "R-FAR", FindingSeverity.Critical),
        };

        var result = await reducer.ReduceAsync(input, []);

        Assert.DoesNotContain(result.Selected, f => f.RuleId == "R-FAR");   // Stufe 2 nie im Prompt
        Assert.Contains(result.Selected, f => f.RuleId == "R-FILE");        // Stufe 1 schon
        Assert.Equal(2, result.PreExisting.Count);                          // Stufe 1 + 2 im Bericht
        Assert.Equal("R-FAR", result.PreExisting[0].RuleId);                // Severity-sortiert
    }
```

Die vier bestehenden Tests der Datei auf den neuen Rückgabetyp umstellen: `result` → `result.Selected` in `Reduce_dedupesIdenticalLocationRuleCategory`, `Reduce_sortsBySeverityDesc_thenInDiffFirst`, `Reduce_capsPerCategory` und `Reduce_isInputOrderIndependent`. In `Reduce_sortsBySeverityDesc_thenInDiffFirst` muss `critInDiff` über `Tiered(..., inDiff: true)` erzeugt werden, damit `InChangedFile` konsistent gesetzt ist; die Erwartung dreht sich auf `R3` (InDiff) vor `R2` (Critical).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter DeterministicFindingReducerTests`
Expected: Kompilierfehler — `FindingReduction` existiert nicht, `ReduceAsync` liefert eine Liste.

- [ ] **Step 3: Write minimal implementation**

`src/Naudit.Core/Models/FindingReduction.cs` (neu):

```csharp
namespace Naudit.Core.Models;

/// <summary>Ergebnis der Verdichtung. <paramref name="Selected"/> geht als Einzelbefunde in den
/// Prompt; <paramref name="PreExisting"/> traegt ALLE Funde ausserhalb des Diffs (ungekappt) fuer
/// den aggregierten Altlasten-Bericht. Beide Listen sind bereits dedupliziert.</summary>
/// <param name="Selected">Funde auf kommentierbaren Diff-Zeilen plus ein kleines Kontingent aus
/// geaenderten Dateien; Funde aus unberuehrten Dateien sind hier nie enthalten.</param>
/// <param name="PreExisting">Alle Funde mit <c>InDiff == false</c>, Severity-absteigend.</param>
public sealed record FindingReduction(
    IReadOnlyList<ScanFinding> Selected,
    IReadOnlyList<ScanFinding> PreExisting)
{
    public static readonly FindingReduction Empty = new([], []);
}
```

`src/Naudit.Core/Abstractions/IFindingReducer.cs`:

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Verdichtet/normalisiert die aggregierten Funde vor dem Grounding. Default deterministisch;
/// optional später LLM-basiert. Liefert eine <see cref="FindingReduction"/>: die Einzelbefunde für den
/// Prompt und getrennt davon alle Altlasten für den aggregierten Bericht.</summary>
public interface IFindingReducer
{
    Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

`src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs` komplett ersetzen:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Sast;

/// <summary>Deterministische Verdichtung: Dedup nach (Datei, Zeile, RuleId, Category), dreistufige
/// Sortierung (Diff-Zeile vor geänderter Datei vor unberührter Datei, dann Severity), getrennte
/// Kontingente pro Category. Altlasten belegen dadurch nie Plätze, die Diff-Befunden zustehen.</summary>
public sealed class DeterministicFindingReducer(
    int maxFindingsPerGroup = 20, int maxPreExistingPerGroup = 5) : IFindingReducer
{
    public Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
    {
        // Kanonische Sortierung EINMAL: Stufe zuerst, dann Severity, dann stabile Tiebreaker.
        // Dadurch ist sowohl die Kappung als auch die Ausgabe unabhaengig von der Input-Reihenfolge.
        var ordered = findings
            .GroupBy(f => (f.FilePath, f.Line, f.RuleId, f.Category))
            .Select(g => g.First())                       // Erstes Vorkommen in Input gewinnt bei Duplikaten
            .OrderBy(Tier)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        // Getrennte Kontingente je Category: Diff-Befunde schoepfen ihres voll aus, bevor
        // Befunde aus geaenderten Dateien ihr eigenes, kleines Kontingent bekommen.
        // Stufe 2 (unberuehrte Dateien) kommt nie einzeln in den Prompt — sie erscheint
        // ausschliesslich aggregiert im Altlasten-Bericht.
        var selected = ordered
            .GroupBy(f => f.Category)
            .SelectMany(g => g.Where(f => f.InDiff).Take(maxFindingsPerGroup)
                .Concat(g.Where(f => !f.InDiff && f.InChangedFile).Take(maxPreExistingPerGroup)))
            .OrderBy(Tier)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        // Ungekappt: der Bericht zaehlt und gruppiert selbst, er braucht die volle Menge.
        // Bewusst NEU nach Severity sortiert statt aus `ordered` uebernommen: dort steht die
        // Stufe vorn, was hier einen Low-Fund aus einer geaenderten Datei vor einen
        // Critical-Fund aus unberuehrtem Code stellen wuerde. Im Bericht zaehlt der Schweregrad.
        var preExisting = ordered
            .Where(f => !f.InDiff)
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.Category)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId)
            .ThenBy(f => f.Tool)
            .ToList();

        return Task.FromResult(new FindingReduction(selected, preExisting));
    }

    // 0 = auf kommentierbarer Diff-Zeile, 1 = in geänderter Datei außerhalb der Hunks,
    // 2 = in unberührter Datei.
    private static int Tier(ScanFinding f) => f.InDiff ? 0 : f.InChangedFile ? 1 : 2;
}
```

`src/Naudit.Infrastructure/Sast/SastOptions.cs`, unter `MaxFindingsPerGroup`:

```csharp
    /// <summary>Cap pro Category in der Verdichtung — gilt für Funde auf kommentierbaren Diff-Zeilen.</summary>
    public int MaxFindingsPerGroup { get; set; } = 20;

    /// <summary>Eigenes, kleines Cap pro Category für vorbestehende Funde in einer im MR geänderten
    /// Datei (außerhalb der Hunks). Getrennt vom Diff-Kontingent, damit Altlasten nie Plätze
    /// belegen, die Diff-Befunden zustehen. Funde in unberührten Dateien kommen gar nicht einzeln
    /// in den Prompt — sie erscheinen nur im aggregierten Altlasten-Bericht
    /// (<c>Naudit:Review:PreExisting</c>).</summary>
    public int MaxPreExistingPerGroup { get; set; } = 5;
```

`src/Naudit.Infrastructure/DependencyInjection.cs:292`:

```csharp
        services.AddSingleton<IFindingReducer>(_ => new DeterministicFindingReducer(
            sastOptions.MaxFindingsPerGroup, sastOptions.MaxPreExistingPerGroup));
```

`tests/Naudit.Tests/Fakes/FakeFindingReducer.cs`:

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Identitäts-Reducer: isoliert die ReviewService-Tests von der Verdichtungslogik.
// Selected = alles, PreExisting = alles ohne InDiff — dieselbe Aufteilungsregel wie der echte.
internal sealed class FakeFindingReducer : IFindingReducer
{
    public Task<FindingReduction> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default)
        => Task.FromResult(new FindingReduction(findings, findings.Where(f => !f.InDiff).ToList()));
}
```

In `ReviewService.RunAnalyzersAsync` den Rückgabetyp auf `FindingReduction` heben und `GatherGroundingAsync` entsprechend anpassen (der Tupel-Eintrag `Findings` wird zu `FindingReduction Reduction`); `ReviewAsync` nutzt vorerst nur `reduction.Selected` für `redFindings` — `PreExisting` wird in Task 4 und 6 verbraucht. Der `needCheckout == false`- und der Checkout-Fehler-Pfad liefern `FindingReduction.Empty` statt `[]`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build Naudit.slnx && dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "DeterministicFindingReducerTests|ReviewServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(sast): Diff-Befunde vor Altlasten priorisieren und getrennt kappen

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 3: `PreExistingSummary` — deterministische Verdichtung

Der Kern der Auswertung: aus bis zu mehreren tausend Altlast-Funden eine Darstellung machen, die in einen Prompt und in einen Kommentar passt, ohne echte Sicherheitsbefunde zu verstecken.

**Files:**
- Create: `src/Naudit.Core/Review/PreExistingSummary.cs`
- Modify: `src/Naudit.Core/Review/ReviewOptions.cs`
- Test: `tests/Naudit.Tests/PreExistingSummaryTests.cs` (neu)

**Interfaces:**
- Consumes: `FindingReduction.PreExisting` aus Task 2.
- Produces (von Task 4 und 6 verbraucht):
  - `PreExistingOptions { bool Enabled; FindingSeverity DetailSeverity; int MaxDetailed; int MaxRules; bool FirstReviewOnly; }`, erreichbar als `ReviewOptions.PreExisting`.
  - `record SeverityCount(FindingSeverity Severity, int Count)`
  - `record PreExistingRuleGroup(string Rule, FindingCategory Category, FindingSeverity Severity, int Count, string? ExampleFilePath, int? ExampleLine)`
  - `record PreExistingSummary(int Total, IReadOnlyList<SeverityCount> BySeverity, IReadOnlyList<ScanFinding> Detailed, int DetailedOmitted, IReadOnlyList<PreExistingRuleGroup> Groups, int GroupedTotal, int GroupsOmitted)` mit `PreExistingSummary.Empty` und `bool IsEmpty`
  - `static PreExistingSummary PreExistingSummary.Build(IReadOnlyList<ScanFinding> preExisting, PreExistingOptions options)`

- [ ] **Step 1: Write the failing test**

`tests/Naudit.Tests/PreExistingSummaryTests.cs` (neu):

```csharp
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PreExistingSummaryTests
{
    private static ScanFinding F(FindingSeverity sev, string rule, string file, int line,
        FindingCategory cat = FindingCategory.Sast)
        => new("opengrep", cat, sev, "msg", rule, file, line);

    [Fact]
    public void Build_listsHighAndCritical_individually_withFileAndLine()
    {
        // Der Fall, der die Verdichtung rechtfertigt: ein echtes Sicherheitsproblem ausserhalb
        // geaenderter Dateien darf NICHT im Regel-Aggregat verschwinden.
        var findings = new[]
        {
            F(FindingSeverity.High, "detected-google-oauth-access-token", "apps/web/calendso.yaml", 402),
            F(FindingSeverity.Medium, "i18next-key-format", "a.tsx", 1),
        };

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        var detailed = Assert.Single(summary.Detailed);
        Assert.Equal("detected-google-oauth-access-token", detailed.RuleId);
        Assert.Equal("apps/web/calendso.yaml", detailed.FilePath);
        Assert.Equal(402, detailed.Line);
        Assert.DoesNotContain(summary.Groups, g => g.Severity >= FindingSeverity.High);
    }

    [Fact]
    public void Build_groupsLowerSeverities_byRule_withCountAndExample()
    {
        var findings = Enumerable.Range(1, 1763)
            .Select(i => F(FindingSeverity.Medium, "i18next-key-format", $"f{i}.tsx", i))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        var group = Assert.Single(summary.Groups);
        Assert.Equal("i18next-key-format", group.Rule);
        Assert.Equal(1763, group.Count);
        Assert.Equal("f1.tsx", group.ExampleFilePath);   // niedrigster Pfad = deterministisch
        Assert.Equal(1, group.ExampleLine);
        Assert.Equal(1763, summary.Total);
    }

    [Fact]
    public void Build_countsEverySeverity_evenWhenCapped()
    {
        var findings = Enumerable.Range(0, 21).Select(i => F(FindingSeverity.High, $"H{i}", $"h{i}.cs", i))
            .Concat(Enumerable.Range(0, 2677).Select(i => F(FindingSeverity.Medium, "M", $"m{i}.cs", i)))
            .Concat(Enumerable.Range(0, 119).Select(i => F(FindingSeverity.Info, "I", $"i{i}.cs", i)))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        Assert.Equal(2817, summary.Total);
        Assert.Equal(21, summary.BySeverity.Single(s => s.Severity == FindingSeverity.High).Count);
        Assert.Equal(2677, summary.BySeverity.Single(s => s.Severity == FindingSeverity.Medium).Count);
        Assert.Equal(119, summary.BySeverity.Single(s => s.Severity == FindingSeverity.Info).Count);
        Assert.Equal(21, summary.Detailed.Count);        // Deckel 50 greift nicht
        Assert.Equal(0, summary.DetailedOmitted);
    }

    [Fact]
    public void Build_capsDetailedList_andReportsRemainderAsOmitted()
    {
        var findings = Enumerable.Range(0, 60)
            .Select(i => F(FindingSeverity.Critical, $"C{i:D2}", $"c{i:D2}.cs", i)).ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions { MaxDetailed = 50 });

        Assert.Equal(50, summary.Detailed.Count);
        Assert.Equal(10, summary.DetailedOmitted);
    }

    [Fact]
    public void Build_capsRuleGroups_andReportsOmittedRuleCount()
    {
        var findings = Enumerable.Range(0, 48)
            .SelectMany(r => Enumerable.Range(0, 48 - r)
                .Select(i => F(FindingSeverity.Medium, $"R{r:D2}", $"r{r:D2}-{i}.cs", i)))
            .ToList();

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions { MaxRules = 30 });

        Assert.Equal(30, summary.Groups.Count);
        Assert.Equal(18, summary.GroupsOmitted);
        Assert.Equal(findings.Count, summary.GroupedTotal);   // Zaehler bleibt vollstaendig
        Assert.Equal("R00", summary.Groups[0].Rule);          // haeufigste Regel zuerst
    }

    [Fact]
    public void Build_onEmptyInput_returnsEmpty()
    {
        var summary = PreExistingSummary.Build([], new PreExistingOptions());

        Assert.True(summary.IsEmpty);
        Assert.Equal(0, summary.Total);
    }

    [Fact]
    public void Build_usesRuleIdFallback_whenFindingHasNoRule()
    {
        var findings = new[] { new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.Medium, "msg") };

        var summary = PreExistingSummary.Build(findings, new PreExistingOptions());

        Assert.Equal("trivy", Assert.Single(summary.Groups).Rule);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PreExistingSummaryTests`
Expected: Kompilierfehler — `PreExistingSummary` und `PreExistingOptions` existieren nicht.

- [ ] **Step 3: Write minimal implementation**

In `src/Naudit.Core/Review/ReviewOptions.cs` als weitere Property von `ReviewOptions`:

```csharp
    /// <summary>Vorbestehende Werkzeugbefunde (Altlasten): aggregierter Baseline-Block im Prompt
    /// und als eigener MR/PR-Kommentar (Naudit:Review:PreExisting).</summary>
    public PreExistingOptions PreExisting { get; set; } = new();
```

und am Ende der Datei:

```csharp
/// <summary>Aggregation der Werkzeugbefunde, die NICHT aus dem Diff stammen. Beeinflusst das
/// Verdict nie — das Gate rechnet weiterhin ausschließlich über die LLM-Findings.</summary>
public sealed class PreExistingOptions
{
    /// <summary>Baseline-Sektion im Prompt und Altlasten-Kommentar. Default AN;
    /// false ⇒ weder Sektion noch Kommentar (heutiges Verhalten).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ab dieser Severity werden Altlasten EINZELN mit Datei:Zeile ausgewiesen statt nach
    /// Regel verdichtet. Default High: was blocken könnte, wenn es im Diff läge, wird auch
    /// außerhalb namentlich genannt.</summary>
    public FindingSeverity DetailSeverity { get; set; } = FindingSeverity.High;

    /// <summary>Deckel für die Einzelliste. Darüber hinaus fällt der Rest ins Regel-Aggregat —
    /// Schutz gegen ein Repo mit hunderten High-Altlasten.</summary>
    public int MaxDetailed { get; set; } = 50;

    /// <summary>Deckel für die nach Regel verdichteten Gruppen.</summary>
    public int MaxRules { get; set; } = 30;

    /// <summary>Altlasten ändern sich zwischen zwei Pushes am selben PR nicht — den Kommentar
    /// deshalb nur beim ersten Review posten. false ⇒ bei jedem Review.</summary>
    public bool FirstReviewOnly { get; set; } = true;
}
```

`src/Naudit.Core/Review/PreExistingSummary.cs` (neu):

```csharp
using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Zähler je Schweregrad, absteigend sortiert.</summary>
public sealed record SeverityCount(FindingSeverity Severity, int Count);

/// <summary>Eine nach Regel verdichtete Gruppe von Altlasten. Beispielort ist deterministisch der
/// kleinste Pfad/die kleinste Zeile der Gruppe.</summary>
public sealed record PreExistingRuleGroup(
    string Rule, FindingCategory Category, FindingSeverity Severity, int Count,
    string? ExampleFilePath, int? ExampleLine);

/// <summary>Deterministische Verdichtung der Funde außerhalb des Diffs.
///
/// Die Verdichtung ist bewusst <b>severity-abhängig</b>: Funde ab
/// <see cref="PreExistingOptions.DetailSeverity"/> bleiben einzeln mit Datei und Zeile stehen,
/// alles darunter wird nach Regel gruppiert. Grund: bei einem realen Repo (cal.com, voller
/// Opengrep-Regelbaum) verteilen sich 2.817 Funde auf 48 Regeln, wobei eine einzige Lint-Regel
/// 62,6 % stellt — uniformes Top-N würde ein echtes Sicherheitsproblem in unberührtem Code hinter
/// i18n-Rauschen verstecken. Die gesamte High-Ebene sind dort 21 Zeilen.</summary>
public sealed record PreExistingSummary(
    int Total,
    IReadOnlyList<SeverityCount> BySeverity,
    IReadOnlyList<ScanFinding> Detailed,
    int DetailedOmitted,
    IReadOnlyList<PreExistingRuleGroup> Groups,
    int GroupedTotal,
    int GroupsOmitted)
{
    public static readonly PreExistingSummary Empty = new(0, [], [], 0, [], 0, 0);

    public bool IsEmpty => Total == 0;

    public static PreExistingSummary Build(IReadOnlyList<ScanFinding> preExisting, PreExistingOptions options)
    {
        if (preExisting.Count == 0)
            return Empty;

        var bySeverity = preExisting
            .GroupBy(f => f.Severity)
            .Select(g => new SeverityCount(g.Key, g.Count()))
            .OrderByDescending(s => s.Severity)
            .ToList();

        // Einzelliste und Rest in EINEM Durchlauf ueber die kanonisch sortierte Menge aufteilen.
        // Bewusst kein Set-Abzug: ScanFinding ist ein record mit Wertgleichheit — zwei gleich
        // befuellte Funde waeren "derselbe" und der Rest damit zu klein. Ueber die Position in
        // der Sortierung geht es exakt und ohne Comparer-Akrobatik. Funde, die der Deckel
        // schluckt, fallen in die Gruppierung: sie duerfen nicht spurlos verschwinden.
        var detailed = new List<ScanFinding>();
        var rest = new List<ScanFinding>();
        var detailedOmitted = 0;
        var maxDetailed = Math.Max(0, options.MaxDetailed);
        foreach (var f in preExisting
                     .OrderByDescending(f => f.Severity)
                     .ThenBy(f => f.FilePath)
                     .ThenBy(f => f.Line)
                     .ThenBy(f => f.RuleId))
        {
            if (f.Severity < options.DetailSeverity)
                rest.Add(f);
            else if (detailed.Count < maxDetailed)
                detailed.Add(f);
            else
            {
                rest.Add(f);
                detailedOmitted++;
            }
        }

        var groupsAll = rest
            .GroupBy(f => (Rule: f.RuleId ?? f.Tool, f.Category))
            .Select(g =>
            {
                var example = g.OrderBy(f => f.FilePath).ThenBy(f => f.Line).First();
                return new PreExistingRuleGroup(g.Key.Rule, g.Key.Category,
                    g.Max(f => f.Severity), g.Count(), example.FilePath, example.Line);
            })
            .OrderByDescending(g => g.Severity)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Rule)
            .ToList();
        var groups = groupsAll.Take(Math.Max(0, options.MaxRules)).ToList();

        return new PreExistingSummary(
            preExisting.Count, bySeverity,
            detailed, detailedOmitted,
            groups, rest.Count, groupsAll.Count - groups.Count);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PreExistingSummaryTests`
Expected: PASS (7 Tests).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(review): Altlasten severity-bewusst zu einer deterministischen Uebersicht verdichten

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 4: Prompt-Sektion „Repository baseline"

Die verdichtete Übersicht als Kontext in den Prompt — 10 bis 50 Zeilen für die gesamte Repo-Lage, statt fünf zufälliger Einzeltreffer. „1.763× `i18next-key-format`" sagt dem Modell, dass das Hauskonvention ist und kein Fund; „6× `insecure-innerhtml`" sagt ihm, dass ein weiteres `innerHTML` im Diff relevant wäre.

**Files:**
- Modify: `src/Naudit.Core/Review/PromtBuilder.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Test: `tests/Naudit.Tests/PromtBuilderTests.cs`

**Interfaces:**
- Consumes: `PreExistingSummary` aus Task 3, `FindingReduction.PreExisting` aus Task 2.
- Produces: `PromptBuilder.Build(..., PreExistingSummary? baseline = null)` — neuer optionaler letzter Parameter, damit alle bestehenden Aufrufe unverändert kompilieren. Die Sektion steht **nach** den Einzelbefunden und **vor** der Tool-Guidance.

- [ ] **Step 1: Write the failing test**

In `tests/Naudit.Tests/PromtBuilderTests.cs` ans Ende der Klasse:

```csharp
    [Fact]
    public void Build_rendersBaselineSection_withDetailedHighAndGroupedRest()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ -1 +1 @@\n+x") };
        var baseline = PreExistingSummary.Build(
            [
                new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "msg",
                    "detected-google-oauth-access-token", "apps/web/calendso.yaml", 402),
                .. Enumerable.Range(0, 1763).Select(i => new ScanFinding("opengrep", FindingCategory.Sast,
                    FindingSeverity.Medium, "msg", "i18next-key-format", $"f{i}.tsx", i)),
            ],
            new PreExistingOptions());

        var text = PromptBuilder.Build("SYS", request, changes, baseline: baseline)[1].Text;

        Assert.Contains("# Repository baseline", text);
        Assert.Contains("NOT introduced by this MR", text);
        Assert.Contains("apps/web/calendso.yaml:402", text);          // schwerer Fund bleibt verortet
        Assert.Contains("i18next-key-format", text);
        Assert.Contains("1763", text);                                 // Rest nur als Zaehler
        Assert.DoesNotContain("f500.tsx", text);                       // keine Einzeltreffer der Gruppe
    }

    [Fact]
    public void Build_withoutBaseline_leavesPromptUnchanged()
    {
        var request = new ReviewRequest("1", 42, "T");
        var changes = new[] { new CodeChange("a.cs", "@@ -1 +1 @@\n+x") };

        var withNull = PromptBuilder.Build("SYS", request, changes)[1].Text;
        var withEmpty = PromptBuilder.Build("SYS", request, changes, baseline: PreExistingSummary.Empty)[1].Text;

        Assert.DoesNotContain("Repository baseline", withNull);
        Assert.Equal(withNull, withEmpty);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PromptBuilderTests`
Expected: Kompilierfehler — `Build` kennt keinen `baseline`-Parameter.

- [ ] **Step 3: Write minimal implementation**

`src/Naudit.Core/Review/PromtBuilder.cs` — Signatur und Aufrufkette:

```csharp
    public static IList<ChatMessage> Build(
        string systemPrompt, ReviewRequest request, IReadOnlyList<CodeChange> changes,
        IReadOnlyList<ScanFinding>? findings = null, ReviewContext? context = null,
        IReadOnlyList<MemoryEntry>? memory = null, bool toolsAvailable = false, string? guidelines = null,
        PreExistingSummary? baseline = null)
```

und im Rumpf direkt nach `AppendFindings(sb, findings ?? []);`:

```csharp
        AppendBaseline(sb, baseline);
```

Dazu die Renderer-Methode (englisch, wie der übrige Prompt):

```csharp
    // Repo-Lage als Kontext, NICHT als Arbeitsliste: die schweren Funde bleiben verortet, der
    // Rest steht nur als Regel + Anzahl. Ein Zaehler wie "1763x i18next-key-format" sagt dem
    // Modell, dass das Hauskonvention ist und kein Fund — Einzeltreffer taeten das nicht.
    private static void AppendBaseline(StringBuilder sb, PreExistingSummary? baseline)
    {
        if (baseline is null || baseline.IsEmpty)
            return;

        sb.AppendLine();
        sb.AppendLine("# Repository baseline (pre-existing tool findings, NOT introduced by this MR)");
        var counts = string.Join(", ", baseline.BySeverity.Select(s => $"{s.Count} {s.Severity.ToString().ToLowerInvariant()}"));
        sb.AppendLine($"{baseline.Total} findings already in the repository: {counts}.");
        sb.AppendLine("Do NOT report these as findings of this MR. Use them as context: a rule with a high count is " +
            "an established project pattern, not a defect; a known weakness class means a new occurrence in the diff matters.");

        if (baseline.Detailed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Highest severity, individually ({baseline.Detailed.Count}" +
                (baseline.DetailedOmitted > 0 ? $" of {baseline.Detailed.Count + baseline.DetailedOmitted}" : "") + ")");
            foreach (var f in baseline.Detailed)
            {
                var loc = f.FilePath is null ? "" : f.Line is int ln ? $" · {f.FilePath}:{ln}" : $" · {f.FilePath}";
                var rule = f.RuleId is null ? "" : $" · {f.RuleId}";
                sb.AppendLine($"- [{f.Severity.ToString().ToUpperInvariant()}] {f.Tool}{rule}{loc}");
            }
        }

        if (baseline.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"## Remaining, grouped by rule ({baseline.GroupedTotal} findings" +
                (baseline.GroupsOmitted > 0 ? $", {baseline.GroupsOmitted} further rules omitted" : "") + ")");
            foreach (var g in baseline.Groups)
            {
                var example = g.ExampleFilePath is null ? "" :
                    g.ExampleLine is int ln ? $", e.g. {g.ExampleFilePath}:{ln}" : $", e.g. {g.ExampleFilePath}";
                sb.AppendLine($"- {g.Rule} ({g.Severity.ToString().ToUpperInvariant()}) — {g.Count}x{example}");
            }
        }
    }
```

In `ReviewService.ReviewAsync` die Übersicht bauen und mitgeben. Nach dem Redaction-Block:

```csharp
        // Altlasten-Uebersicht: rein deterministisch aus den Funden, kein LLM. Fail-open —
        // eine kaputte Verdichtung darf das Review nicht kippen.
        var baseline = options.PreExisting.Enabled
            ? SafeBuildBaseline(reduction.PreExisting)
            : PreExistingSummary.Empty;
```

mit

```csharp
    // Fail-open wie das uebrige Grounding: ohne Uebersicht laeuft der Review einfach ohne
    // Baseline-Sektion und ohne Altlasten-Kommentar weiter.
    private PreExistingSummary SafeBuildBaseline(IReadOnlyList<ScanFinding> preExisting)
    {
        try { return PreExistingSummary.Build(preExisting, options.PreExisting); }
        catch (Exception) { return PreExistingSummary.Empty; }
    }
```

und im `PromptBuilder.Build`-Aufruf `baseline: baseline` ergänzen.

**Redaction:** Die Baseline trägt nur Regel-Id, Pfad, Zeile, Tool und Zähler — **keine** Fund-Nachricht. Deshalb ist hier keine zusätzliche Redaction nötig; das ist bewusst so und der Grund, warum die Nachricht nicht gerendert wird. Als Kommentar an `AppendBaseline` vermerken.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "PromptBuilderTests|ReviewServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(prompt): Repo-Baseline als aggregierte Kontext-Sektion in den Prompt

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 5: `IGitPlatform.PostNoteAsync` auf beiden Plattformen

Der Altlasten-Bericht ist ein eigenständiger MR/PR-Kommentar ohne Inline-Position. GitLab postet die Summary heute schon so; GitHub trägt sie im Review-Body, braucht für eine freistehende Notiz also den Issue-Comments-Endpunkt.

**Files:**
- Modify: `src/Naudit.Core/Abstractions/IGitPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`
- Modify: `tools/Naudit.Benchmark/CapturingGitPlatform.cs`
- Test: `tests/Naudit.Tests/GitLabPlatformTests.cs`, `tests/Naudit.Tests/GitHubPlatformTests.cs`

**Interfaces:**
- Produces: `Task IGitPlatform.PostNoteAsync(ReviewRequest request, string markdown, CancellationToken ct = default)`.
  Von Task 6 verbraucht. GitLab: `POST api/v4/projects/{ProjectId}/merge_requests/{Iid}/notes`, Body `{ body }`. GitHub: `POST repos/{ProjectId}/issues/{Iid}/comments`, Body `{ body }` (Issue-Comments-Endpunkt — PRs sind auf GitHub Issues, und nur dieser Endpunkt erzeugt eine freistehende Notiz ohne Review-Kontext).
- `FakeGitPlatform` bekommt `public List<string> PostedNotes { get; } = [];`

- [ ] **Step 1: Write the failing test**

Ans Ende von `tests/Naudit.Tests/GitLabPlatformTests.cs` (nutzt die dort bestehenden Helfer `Tokens()`, `Opts()`, `ClientReturning(...)` und `Request` = `new("7", 42, "Title")`):

```csharp
    [Fact]
    public async Task PostNoteAsync_postsStandaloneNote()
    {
        var capture = new StubHttpMessageHandler(_ => Ok());
        var platform = new GitLabPlatform(
            ClientReturning(HttpStatusCode.Created, "{}", capture), Tokens(), Opts());

        await platform.PostNoteAsync(Request, "**Altlasten**");

        var call = Assert.Single(capture.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://gitlab.example.com/api/v4/projects/7/merge_requests/42/notes",
            call.Uri!.ToString());
        Assert.Contains("Altlasten", call.Body);
    }
```

Ans Ende von `tests/Naudit.Tests/GitHubPlatformTests.cs` (`Request` ist dort `new("octo/hello-world", 42, "Title")`):

```csharp
    [Fact]
    public async Task PostNoteAsync_postsIssueComment()
    {
        var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });
        // Logger-Parameter ist optional (Default NullLogger) — wie in den bestehenden Tests weglassen.
        var platform = new GitHubPlatform(
            ClientReturning(HttpStatusCode.Created, "{}", capture), Tokens(), Opts());

        await platform.PostNoteAsync(Request, "**Altlasten**");

        var call = Assert.Single(capture.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal("https://api.github.com/repos/octo/hello-world/issues/42/comments",
            call.Uri!.ToString());
        Assert.Contains("Altlasten", call.Body);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitLabPlatformTests|GitHubPlatformTests"`
Expected: Kompilierfehler — `PostNoteAsync` existiert nicht.

- [ ] **Step 3: Write minimal implementation**

`src/Naudit.Core/Abstractions/IGitPlatform.cs`, nach `PostReviewAsync`:

```csharp
    /// <summary>Postet einen eigenständigen MR/PR-Kommentar ohne Inline-Position — für Inhalte,
    /// die sich an keiner Diff-Zeile verankern lassen (Altlasten-Bericht). Bewusst getrennt von
    /// <see cref="PostReviewAsync"/>: der Bericht gehört nicht in die Review-Summary und trägt
    /// keine Kommentar-Ids, die zugeordnet werden müssten.</summary>
    Task PostNoteAsync(ReviewRequest request, string markdown, CancellationToken ct = default);
```

`GitLabPlatform` — dieselbe Notiz-Route, die `PostReviewAsync` schon für die Summary nutzt:

```csharp
    public async Task PostNoteAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        var url = $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}/notes";
        (await SendAsync(HttpMethod.Post, url, request.ProjectId, new { body = markdown }, ct))
            .EnsureSuccessStatusCode();
    }
```

`GitHubPlatform` — PRs sind auf GitHub Issues; nur dieser Endpunkt erzeugt eine freistehende Notiz:

```csharp
    public async Task PostNoteAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        // Issue-Comments-Endpunkt: ein PR IST auf GitHub ein Issue. Der Reviews-Endpunkt wuerde
        // stattdessen einen zweiten Review-Status erzeugen — das ist hier gerade nicht gewollt.
        var url = $"repos/{request.ProjectId}/issues/{request.MergeRequestIid}/comments";
        using var response = await SendAsync(HttpMethod.Post, url, request.ProjectId, new { body = markdown }, ct);
        response.EnsureSuccessStatusCode();
    }
```

`FakeGitPlatform`:

```csharp
    public List<string> PostedNotes { get; } = [];

    public Task PostNoteAsync(ReviewRequest request, string markdown, CancellationToken ct = default)
    {
        PostedNotes.Add(markdown);
        return Task.CompletedTask;
    }
```

`tools/Naudit.Benchmark/CapturingGitPlatform.cs` analog erweitern (Notiz mitschneiden, kein Netz) — die Datei ist die vierte `IGitPlatform`-Implementierung und würde sonst den Build brechen.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet build Naudit.slnx && dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "GitLabPlatformTests|GitHubPlatformTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(git): PostNoteAsync fuer freistehende MR/PR-Kommentare auf beiden Plattformen

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 6: Altlasten-Kommentar posten (nur beim ersten Review)

**Files:**
- Create: `src/Naudit.Core/Review/PreExistingReport.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`
- Test: `tests/Naudit.Tests/PreExistingReportTests.cs` (neu), `tests/Naudit.Tests/ReviewServiceTests.cs`

**Interfaces:**
- Consumes: `PreExistingSummary` (Task 3), `IGitPlatform.PostNoteAsync` (Task 5), `IReviewRoundtripCounter` (bestehend).
- Produces: `static string PreExistingReport.Markdown(PreExistingSummary summary)` — deutscher Markdown, wie `ComposeSummary`.

- [ ] **Step 1: Write the failing test**

`tests/Naudit.Tests/PreExistingReportTests.cs` (neu):

```csharp
using Naudit.Core.Models;
using Naudit.Core.Review;
using Xunit;

namespace Naudit.Tests;

public class PreExistingReportTests
{
    [Fact]
    public void Markdown_namesHighFindings_andCountsTheRest()
    {
        var summary = PreExistingSummary.Build(
            [
                new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "msg",
                    "detected-generic-api-key", ".env.example", 72),
                .. Enumerable.Range(0, 1763).Select(i => new ScanFinding("opengrep", FindingCategory.Sast,
                    FindingSeverity.Medium, "msg", "i18next-key-format", $"f{i}.tsx", i)),
            ],
            new PreExistingOptions());

        var md = PreExistingReport.Markdown(summary);

        Assert.Contains("Vorbestehende Werkzeugbefunde", md);
        Assert.Contains("1764", md);                          // Gesamtzahl
        Assert.Contains(".env.example:72", md);               // High einzeln, verortet
        Assert.Contains("detected-generic-api-key", md);
        Assert.Contains("i18next-key-format", md);
        Assert.Contains("1763", md);                          // Rest als Zaehler
        Assert.Contains("nicht durch diesen", md);            // Abgrenzung zum MR
        Assert.DoesNotContain("Verdict", md);                 // beeinflusst die Merge-Entscheidung nie
    }
}
```

In `tests/Naudit.Tests/ReviewServiceTests.cs`:

```csharp
    private static FakeSastAnalyzer PreExistingAnalyzer() => new("opengrep",
    [
        new ScanFinding("opengrep", FindingCategory.Sast, FindingSeverity.High, "msg", "R-ALT", "fremd.cs", 9),
    ]);

    [Fact]
    public async Task ReviewAsync_postsPreExistingReport_asSeparateNote_onFirstReview()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers: [PreExistingAnalyzer()], roundtrips: new FakeRoundtripCounter(0));

        await service.ReviewAsync(Request);

        var note = Assert.Single(git.PostedNotes);
        Assert.Contains("fremd.cs:9", note);
        Assert.DoesNotContain("fremd.cs:9", git.PostedMarkdown!);   // nicht in der Summary
    }

    [Fact]
    public async Task ReviewAsync_skipsPreExistingReport_onLaterReviews()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@\n+x")]);
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers: [PreExistingAnalyzer()], roundtrips: new FakeRoundtripCounter(1));

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedNotes);   // Altlasten aendern sich zwischen Pushes nicht
    }

    [Fact]
    public async Task ReviewAsync_doesNotPostReport_whenFeatureDisabled()
    {
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@\n+x")]);
        var options = new ReviewOptions { SystemPrompt = "SYS" };
        options.PreExisting.Enabled = false;
        var service = CreateService(chat, git, options, analyzers: [PreExistingAnalyzer()]);

        await service.ReviewAsync(Request);

        Assert.Empty(git.PostedNotes);
        Assert.DoesNotContain("Repository baseline", chat.LastMessages![1].Text);
    }

    [Fact]
    public async Task ReviewAsync_reportFailure_doesNotFailTheReview()
    {
        // Der Review ist zu diesem Zeitpunkt bereits gepostet — ein Fehler am Zusatzkommentar
        // darf das Ergebnis nicht mehr kippen.
        var chat = new FakeChatClient("""{"summary":"ok","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -1 +1 @@\n+x")]) { NoteError = new InvalidOperationException("boom") };
        var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            analyzers: [PreExistingAnalyzer()]);

        var result = await service.ReviewAsync(Request);

        Assert.Equal(ReviewVerdict.Approve, result.Verdict);
    }
```

Dafür `FakeGitPlatform` um `public Exception? NoteError { get; set; }` erweitern; `PostNoteAsync` wirft dann diese Ausnahme.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "PreExistingReportTests|ReviewServiceTests"`
Expected: FAIL — `PreExistingReport` fehlt, `PostedNotes` bleibt leer.

- [ ] **Step 3: Write minimal implementation**

`src/Naudit.Core/Review/PreExistingReport.cs` (neu):

```csharp
using System.Text;
using Naudit.Core.Models;

namespace Naudit.Core.Review;

/// <summary>Rendert die Altlasten-Übersicht als eigenständigen MR/PR-Kommentar (deutsch, wie die
/// Summary). Bewusst getrennt von der englischen Prompt-Sektion: gleiche Verdichtung, zwei
/// Zielgruppen. Der Bericht nennt nie ein Verdikt — Altlasten beeinflussen die Merge-Entscheidung
/// nicht.</summary>
public static class PreExistingReport
{
    public static string Markdown(PreExistingSummary s)
    {
        var sb = new StringBuilder();
        var counts = string.Join(", ", s.BySeverity.Select(x => $"{x.Count} {x.Severity}"));
        sb.AppendLine($"### 🗂️ Vorbestehende Werkzeugbefunde ({s.Total})");
        sb.AppendLine();
        sb.AppendLine($"Diese Funde stammen aus dem Repository-Scan und sind **nicht durch diesen Merge Request verursacht** — sie beeinflussen die Merge-Entscheidung nicht. Verteilung: {counts}.");

        if (s.Detailed.Count > 0)
        {
            sb.AppendLine();
            var head = s.DetailedOmitted > 0
                ? $"**Höchste Schweregrade** (erste {s.Detailed.Count} von {s.Detailed.Count + s.DetailedOmitted}):"
                : $"**Höchste Schweregrade** ({s.Detailed.Count}):";
            sb.AppendLine(head);
            sb.AppendLine();
            foreach (var f in s.Detailed)
            {
                var loc = f.FilePath is null ? "" : f.Line is int ln ? $"`{f.FilePath}:{ln}` " : $"`{f.FilePath}` ";
                var rule = f.RuleId is null ? f.Tool : f.RuleId;
                sb.AppendLine($"- {loc}{rule} ({f.Severity})");
            }
        }

        if (s.Groups.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("<details>");
            var rest = s.GroupsOmitted > 0
                ? $"Übrige {s.GroupedTotal} Befunde nach Regel (Top {s.Groups.Count}, {s.GroupsOmitted} weitere Regeln nicht gezeigt)"
                : $"Übrige {s.GroupedTotal} Befunde nach Regel ({s.Groups.Count})";
            sb.AppendLine($"<summary>{rest}</summary>");
            sb.AppendLine();
            sb.AppendLine("| Regel | Schweregrad | Anzahl | Beispiel |");
            sb.AppendLine("| --- | --- | ---: | --- |");
            foreach (var g in s.Groups)
            {
                var example = g.ExampleFilePath is null ? "—"
                    : g.ExampleLine is int ln ? $"`{g.ExampleFilePath}:{ln}`" : $"`{g.ExampleFilePath}`";
                sb.AppendLine($"| {g.Rule} | {g.Severity} | {g.Count} | {example} |");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
        }

        return sb.ToString().TrimEnd();
    }
}
```

In `ReviewService.ReviewAsync` den Roundtrip-Zähler entkoppeln (er entscheidet jetzt zwei Dinge) — Zeilen 30–39 ersetzen:

```csharp
        // Der Zaehler traegt zwei Entscheidungen: das Roundtrip-Limit UND ob dies das erste
        // Review am PR ist (der Altlasten-Bericht wird nur einmal gepostet). Deshalb auch fuer
        // CI-Trigger zaehlen, aber nur wenn ueberhaupt jemand das Ergebnis braucht.
        var limitActive = request.Trigger == ReviewTrigger.Webhook && options.MaxRoundtrips > 0;
        var priorReviews = limitActive || options.PreExisting.Enabled
            ? await SafeCountRoundtripsAsync(request, ct)
            : 0;
        if (limitActive && priorReviews >= options.MaxRoundtrips)
            return new ReviewResult(string.Empty, ReviewVerdict.Approve, Skipped: true);
```

und weiter unten `lastRoundtrip` entsprechend:

```csharp
        var lastRoundtrip = limitActive && priorReviews + 1 == options.MaxRoundtrips;
```

Nach `PostReviewAsync` und **vor** `RecordAuditAsync`:

```csharp
        // Eigenstaendiger Kommentar statt Summary-Anhang: der Bericht ist lang und aendert sich
        // zwischen zwei Pushes nicht. Best-effort — der Review ist bereits gepostet.
        if (options.PreExisting.Enabled && !baseline.IsEmpty
            && (!options.PreExisting.FirstReviewOnly || priorReviews == 0))
        {
            try { await gitPlatform.PostNoteAsync(request, PreExistingReport.Markdown(baseline), ct); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* bewusst geschluckt */ }
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: gesamte Suite PASS. Insbesondere müssen `ReviewGateOptionsTests` und die Roundtrip-Tests unverändert grün sein.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(review): Altlasten-Bericht als eigenen Kommentar beim ersten Review posten

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

### Task 7: Settings-Katalog und Dokumentation

**Files:**
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `docs/sast-grounding.md`
- Modify: `docs/configuration.md`
- Test: `tests/Naudit.Tests/PreExistingSummaryTests.cs` (Katalog-Assertion ergänzen, Muster aus `SessionSandboxOptionsTests.cs:51-55`)

**Interfaces:**
- Consumes: die Keys aus Task 2 und 3.
- Produces: nichts für Folge-Tasks — das ist die letzte Aufgabe.

- [ ] **Step 1: Write the failing test**

Ans Ende von `tests/Naudit.Tests/PreExistingSummaryTests.cs`:

```csharp
    [Fact]
    public void NewKeys_areDbManaged()
    {
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Sast:MaxPreExistingPerGroup", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:Enabled", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:DetailSeverity", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:MaxDetailed", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:MaxRules", out _));
        Assert.True(Naudit.Infrastructure.Settings.SettingsCatalog.TryGet("Naudit:Review:PreExisting:FirstReviewOnly", out _));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter PreExistingSummaryTests`
Expected: FAIL — die Keys fehlen im Katalog.

- [ ] **Step 3: Write minimal implementation**

In `SettingsCatalog.All` nach `new("Naudit:Sast:MaxFindingsPerGroup", false),`:

```csharp
        new("Naudit:Sast:MaxPreExistingPerGroup", false),
```

und nach dem `Naudit:Review:Guidelines:*`-Block:

```csharp
        new("Naudit:Review:PreExisting:Enabled", false),
        new("Naudit:Review:PreExisting:DetailSeverity", false,
            AllowedValues: ["Info", "Low", "Medium", "High", "Critical"]),
        new("Naudit:Review:PreExisting:MaxDetailed", false),
        new("Naudit:Review:PreExisting:MaxRules", false),
        new("Naudit:Review:PreExisting:FirstReviewOnly", false),
```

`docs/sast-grounding.md` — die Konfigurationstabelle um `MaxPreExistingPerGroup` erweitern und einen Abschnitt „Pre-existing findings" ergänzen, der die drei Stufen, die severity-abhängige Verdichtung und den eigenen Kommentar beschreibt. Die gemessenen cal.com-Zahlen (2.817 Befunde / 48 Regeln / High-Ebene = 21 Befunde in 7 Regeln) als Begründung aufnehmen — sie erklären, warum nach Regel gruppiert und nicht Top-N gekappt wird.

`docs/configuration.md` — die sechs neuen Keys in die Key-Tabelle (Zeilen ~143–147 für `Naudit:Sast:*`, der `Naudit:Review:*`-Block darunter), jeweils mit Default.

Zusätzlich `CLAUDE.md` im Abschnitt „Request flow" nachziehen: die Verdichtung liefert jetzt `FindingReduction`, und der Altlasten-Bericht geht als eigener `PostNoteAsync`-Kommentar raus.

- [ ] **Step 4: Run the full suite**

Run: `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "docs(sast): Altlasten-Behandlung dokumentieren und Keys DB-verwaltbar machen

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01E1JpSCkkeWvTe54efnPAKf"
```

---

## Abnahme

- **Reducer-Test:** bei 18 High-Altlasten und 21 Medium-Diff-Befunden enthält `Selected` alle 21 Diff-Befunde plus höchstens `MaxPreExistingPerGroup` Altlasten (Task 2, `Reduce_keepsAllDiffFindings_evenWhenOutrankedBySeverity`).
- **Sicherheitsbefund außerhalb geänderter Dateien** bleibt mit Datei und Zeile sichtbar — im Prompt und im Kommentar (Task 3/4/6).
- **Verdict-Logik unverändert:** `ReviewGateOptionsTests` und alle bestehenden `ReviewServiceTests` grün, ohne Anpassung ihrer Erwartungen.
- **Volle Suite grün:** `DOTNET_USE_POLLING_FILE_WATCHER=1 dotnet test Naudit.slnx`.
- **Manuell, vor dem Merge:** ein Review auf cal.com #10600 über `tools/Naudit.Benchmark` mit `Naudit__Sast__Enabled=true` zeigt (a) die Diff-Befunde als Einzelbefunde im geloggten Prompt, (b) die Baseline-Sektion mit den namentlich genannten High-Altlasten, (c) einen Altlasten-Kommentar am PR.
