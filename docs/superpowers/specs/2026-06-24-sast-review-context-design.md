# Design: SAST/SCA-Grounding im Review-Kontext

*2026-06-24 · Projekt: Naudit*

## Ziel

Naudit liest heute **nur den Diff** und reviewt ihn rein semantisch. Das Zwischenfazit
(Modell `nemotron-3-ultra:cloud`) belegt drei Schwächen, die genau hier ansetzen: eine
systematische **.NET-10-Halluzination** (veraltetes Modellwissen), **fehlendes
Dependency-/SCA-Bewusstsein** (transitive CVE NU1903 übersehen) und **schwankender Recall**.
Der mit Abstand klarste Upgrade-Pfad laut Bericht ist **Tool-Augmentation**: reale
SAST/SCA-Funde als Grounding in den Review-Kontext geben, damit das LLM auf der semantischen
Ebene arbeitet, auf der es stark ist, und an realen Signalen verankert wird statt zu halluzinieren.

Diese Iteration: Naudit **klont den MR/PR-Head selbst, lässt mehrere Scanner laufen** und
speist deren normalisierte Funde als **Grounding** in den bestehenden Review. Das Verdict trifft
weiterhin **allein das LLM** — die Tools entscheiden nichts hart (kein Tandem-Gate in dieser Stufe).

### Bewusste Abgrenzung

- **Kein hartes Tool-Gate.** Funde sind Grounding; das eine Verdict bleibt beim LLM. Severity-Gating
  (Bericht-Empfehlung #5) ist ein möglicher Folgeschritt, nicht Teil dieser Iteration.
- **Kein LLM-Reduce-Pass als Default.** Verdichtung läuft deterministisch; ein LLM-Verdichtungs-Pass
  ist als optionaler, zuschaltbarer Schritt hinter einem Seam vorgesehen (BA-Experiment), Default aus.
- **Keine WebSearch / Aktualitäts-Recherche.** Separater späterer Schritt.

## Entscheidungen

- **Naudit scannt selbst** (statt Funde von der CI entgegenzunehmen). Es klont den MR/PR-Head in ein
  wegwerfbares Temp-Verzeichnis und führt die Tools darauf aus. Trade-off bewusst akzeptiert: Naudit
  bekommt Klon-Fähigkeit + Tools im Container; dafür ist die Augmentation unabhängig davon, ob die
  Pipeline etwas mitliefert.
- **Pluggbare Analyzer als Core-Abstraktion** (`ISastAnalyzer`), exakt das Extension-Muster von
  `IGitPlatform`/`IChatClient`: mehrere Implementierungen registriert, per Config an-/abschaltbar,
  **neues Tool = eine Infra-Klasse + Config-Eintrag, kein Core-Eingriff**. Motiviert durch das Ziel,
  später weitere SAST-Tools für höchste Abdeckung zu ergänzen.
- **Core bleibt SDK-/tool-frei.** Core kennt nur die Abstraktionen (`ISastAnalyzer`,
  `IWorkspaceProvider`, `IFindingReducer`) und das Model `ScanFinding`. Alle Tool-/Prozess-Aufrufe
  liegen in Infrastructure. Die zentrale Regel (`Core → MEAI-Abstractions only`) bleibt gewahrt.
- **Tools kombiniert:** Semgrep (SAST, viele Sprachen, kein Build), Trivy `fs` (SCA/Dependency-CVEs,
  viele Ökosysteme), `dotnet list package --vulnerable` (SCA für .NET). Erste zwei führen **fremden
  Code nicht aus** (rein statisch); `dotnet-sca` macht `restore` (Code-Ausführung) → **opt-in**.
- **Grounding-only:** Funde gehen als Kontext in den Prompt; `ReviewService` parst weiter genau ein
  `{summary, verdict}` vom LLM. Keine neue Verdict-Logik.
- **Alle Funde repo-weit** in den Kontext, aber **annotiert** mit `[in diff]` (Datei vom MR berührt)
  vs `[pre-existing]`. Nichts wird weggefiltert; die Annotation lässt LLM und Leser „der MR
  verursacht X" von „Repo hatte Y schon" trennen und mildert die Flutungs-/Alert-Fatigue-Gefahr.
- **Deterministische Verdichtung** (`DeterministicFindingReducer`): Dedup nach
  `(FilePath, Line, RuleId/CVE)`, Gruppierung nach `Category`+Package, Sortierung `Severity`↓ +
  `InDiff`↑, Cap pro Gruppe. Reproduzierbar (wichtig für die BA-Messung), kein Recall-Risiko durch
  einen verdichtenden LLM. Hinter `IFindingReducer`-Seam, damit später ein `LlmFindingReducer`
  config-gewählt an dieselbe Stelle tritt (beide liefern `ScanFinding[]`).
- **Toolchain-Grounding im System-Prompt** („nimm Target-Framework/Toolchain als gültig und aktuell
  an; flagge keine Framework-/SDK-Existenz") → killt die dokumentierte .NET-10-Halluzination quasi
  gratis, unabhängig von den Scannern.
- **Prozess-Naht wiederverwendet:** `IProcessRunner`/`ProcessSpec` (heute unter
  `Ai/ClaudeCode/`) wandern nach `Infrastructure/Process/`; ClaudeCode nutzt sie weiter, die Analyzer
  und der `GitWorkspaceProvider` shellen ebenfalls darüber aus. Testbar via Stub, analog
  `StubHttpMessageHandler`.
- **Rückwärtskompatibel:** `Naudit:Sast:Enabled=false` (oder leere Analyzer-Liste) ⇒ kein Checkout,
  leeres `IEnumerable<ISastAnalyzer>` ⇒ **exakt heutiges diff-only-Verhalten**.
- **Graceful degradation:** ein einzelner Analyzer-Fehler/Timeout wird geloggt und übersprungen;
  schlägt der **Checkout ganz** fehl, degradiert der Review auf diff-only (statt das Gate hart zu
  failen). Bewusst anders als das fail-closed bei mehrdeutigem Verdict — Scan-Infra soll keinen
  validen MR blocken.

## Komponenten

### 1. Core-Model: `ScanFinding` (`Naudit.Core/Models`)

```csharp
public enum FindingCategory { Sast, Sca }
public enum FindingSeverity { Info, Low, Medium, High, Critical }

public sealed record ScanFinding(
    string Tool,                  // "semgrep" | "trivy" | "dotnet-sca" | …
    FindingCategory Category,
    FindingSeverity Severity,
    string Message,
    string? RuleId   = null,      // CVE-…, Semgrep-Rule-ID, NuGet-Advisory
    string? FilePath = null,
    int?    Line     = null)
{
    public bool InDiff { get; init; }   // vom Orchestrator gesetzt
}
```

### 2. Core-Abstraktionen (`Naudit.Core/Abstractions`)

```csharp
// Beschafft den Quellcode eines ReviewRequests lokal; Dispose räumt das Temp-Verzeichnis auf.
public interface IWorkspaceProvider
{
    Task<IReviewWorkspace> CheckoutAsync(ReviewRequest request, CancellationToken ct = default);
}
public interface IReviewWorkspace : IAsyncDisposable { string RootPath { get; } }

// Pluggbarer Code-Scanner (SAST/SCA). Mehrere registriert. Nicht anwendbar ⇒ leere Liste.
public interface ISastAnalyzer
{
    string Name { get; }
    Task<IReadOnlyList<ScanFinding>> AnalyzeAsync(
        IReviewWorkspace workspace, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}

// Verdichtungs-Seam. Default deterministisch; optional später LLM-basiert. Liefert ScanFinding[].
public interface IFindingReducer
{
    Task<IReadOnlyList<ScanFinding>> ReduceAsync(
        IReadOnlyList<ScanFinding> findings, IReadOnlyList<CodeChange> changes, CancellationToken ct = default);
}
```

### 3. Infrastructure-Implementierungen

- **`Infrastructure/Process/`** — `IProcessRunner`/`ProcessSpec`/`ProcessResult`/`SystemProcessRunner`
  aus `Ai/ClaudeCode/` hierher verschoben (kleiner, gerechtfertigter Refactor; ClaudeCode-Adapter
  zieht den Namespace nach). Gemeinsame Prozess-Naht für ClaudeCode, Analyzer und Workspace-Klon.
- **`Infrastructure/Sast/GitWorkspaceProvider`** (`IWorkspaceProvider`) — flacher Klon ins Temp-Dir
  über den Head-Ref der Plattform: GitLab `refs/merge-requests/<iid>/head`, GitHub
  `refs/pull/<n>/head`; `checkout FETCH_HEAD`. Dispose löscht das Verzeichnis. Auth via vorhandenes
  Plattform-Token. ⚠️ **Trickigstes Detail:** Klon-URL-Auflösung — GitLab `ProjectId` ist numerisch,
  braucht ggf. einen API-Call zum `path_with_namespace`; GitHub `ProjectId` ist `owner/repo` und
  direkt klonbar. Eigener, isolierter Implementierungs-Task im Plan.
- **`Infrastructure/Sast/SemgrepAnalyzer`** — `semgrep --config auto --json <root>` → `ScanFinding`
  (`Category.Sast`); mappt Semgrep-Severity → `FindingSeverity`, Datei/Zeile aus dem Result.
- **`Infrastructure/Sast/TrivyAnalyzer`** — `trivy fs --scanners vuln --format json <root>` →
  `ScanFinding` (`Category.Sca`); CVE-ID → `RuleId`, Package/Version in `Message`.
- **`Infrastructure/Sast/DotnetScaAnalyzer`** (opt-in) — `dotnet restore` +
  `dotnet list package --vulnerable --include-transitive --format json` → `ScanFinding`
  (`Category.Sca`). Fängt genau die dokumentierte NU1903-Lücke.
- **`Infrastructure/Sast/DeterministicFindingReducer`** (`IFindingReducer`) — Dedup/Gruppierung/
  Sortierung/Cap wie unter *Entscheidungen* beschrieben. Default-Registrierung.

Jeder Analyzer kapselt sein JSON-Parsing und Severity-Mapping vollständig — die einzige Stelle, die
ein neues Tool kennt.

### 4. Orchestrierung: `ReviewService` (Erweiterung)

Injection wächst um `IWorkspaceProvider`, `IEnumerable<ISastAnalyzer>`, `IFindingReducer`. Ablauf:

```
changes = gitPlatform.GetChangesAsync(request)
if changes leer → ReviewResult("", Approve)           // wie heute
await using ws = workspaceProvider.CheckoutAsync(request)   // Fehler ⇒ ws = null, diff-only
findings = analyzer parallel; je try/catch (Fehler ⇒ Warning, nichts beitragen)
findings = InDiff annotieren (FilePath ∈ geänderte Dateien)
findings = reducer.ReduceAsync(findings, changes)
messages = PromptBuilder.Build(SystemPrompt, request, changes, findings)
response = chatClient.GetResponseAsync(messages, JsonMode, ct)   // wie heute ab hier
→ LlmReviewResponse parsen → Verdict-Mapping (fail-closed) → PostSummaryAsync → ReviewResult
```

### 5. Prompt-Grounding: `PromptBuilder` (Erweiterung)

`Build` bekommt einen Parameter `IReadOnlyList<ScanFinding> findings`. Nach den Datei-Diffs eine
kompakte, annotierte Sektion:

```
# Static-analysis & dependency findings (grounding — tools run on the repo, treat as reliable)
Prioritize [in diff] (introduced/touched by this MR). [pre-existing] were already in the repo.

## Dependency / SCA
- [CRITICAL][pre-existing] trivy · CVE-2023-xxxx · Newtonsoft.Json 9.0.1 → …
## SAST
- [HIGH][in diff] semgrep · csharp.lang.security.sqli · src/Foo.cs:42 → …
```

Keine Funde ⇒ explizite Zeile `No tool findings.` (das Modell weiß: Tools liefen, nichts gefunden —
entzieht Dependency-Halluzinationen den Boden).

**`PromptBuilder.DefaultSystemPrompt`** ergänzt um zwei Sätze:
1. „Static-analysis and dependency-scan results are provided below as grounding; treat them as
   reliable signals."
2. „Assume the project's target framework and toolchain are valid and current; do NOT flag a
   framework/SDK version as nonexistent or unsupported." *(→ killt die .NET-10-Halluzination.)*

### 6. Config (`Naudit:Sast`) & Composition

```
Naudit:Sast:Enabled             = true
Naudit:Sast:Analyzers           = ["semgrep","trivy"]   # dotnet-sca opt-in (baut fremden Code)
Naudit:Sast:Reducer             = "deterministic"        # | "llm" (späterer Seam)
Naudit:Sast:AnalyzerTimeout     = "00:05:00"
Naudit:Sast:MaxFindingsPerGroup = 20
```

`AddNauditInfrastructure` registriert bei `Enabled=true` den `GitWorkspaceProvider`, je gewähltem
Namen den passenden `ISastAnalyzer` und den `IFindingReducer`; `ReviewService` bekommt
`IEnumerable<ISastAnalyzer>` (leer ⇒ diff-only). Bei `Enabled=false` werden keine Scan-Services
registriert.

## Datenfluss

```
ReviewService.ReviewAsync
  → IGitPlatform.GetChangesAsync ............... Diff (wie heute)
  → IWorkspaceProvider.CheckoutAsync .......... Klon MR/PR-Head ins Temp-Dir (git via IProcessRunner)
  → ISastAnalyzer[*].AnalyzeAsync (parallel) .. Semgrep/Trivy/dotnet-sca → ScanFinding[] je Tool
        (Tool via IProcessRunner; JSON parsen; Severity/Datei/Zeile mappen; Fehler ⇒ skip)
  → InDiff annotieren · IFindingReducer.ReduceAsync (dedup/gruppieren/sortieren/cappen)
  → PromptBuilder.Build(system, request, changes, findings) ... Diff + Grounding-Sektion
  → IChatClient.GetResponseAsync(JsonMode) .... { summary, verdict } (wie heute)
  → Verdict-Mapping (fail-closed) · IGitPlatform.PostSummaryAsync · ReviewResult
  → ws.DisposeAsync ........................... Temp-Verzeichnis löschen
```

## Tests (TDD, spiegeln das bestehende Vorgehen)

- **Core (`ReviewServiceTests`, ohne Netz/Prozess):** `FakeWorkspaceProvider` + `FakeSastAnalyzer`
  (kanonische Funde + einer, der wirft) ⇒ Funde landen im Prompt, `InDiff` korrekt annotiert,
  Analyzer-Fehler degradiert (Review läuft weiter), Checkout-Fehler ⇒ diff-only.
- **`DeterministicFindingReducerTests`:** Dedup gleicher `(Datei, Zeile, RuleId)`, Gruppierung,
  Sortierung `Severity`↓/`InDiff`↑, Cap pro Gruppe.
- **Analyzer (`SemgrepAnalyzerTests`/`TrivyAnalyzerTests`/`DotnetScaAnalyzerTests`):**
  `StubProcessRunner` füttert **echte, eingefangene Tool-JSON-Outputs** ⇒ korrektes
  `ScanFinding`-Mapping (Severity/Datei/Zeile/Category/RuleId).
- **`GitWorkspaceProviderTests`:** `StubProcessRunner` prüft die git-Befehlssequenz
  (clone → fetch ref → checkout) und Temp-Cleanup bei Dispose.
- **`PromptBuilderTests`:** Grounding-Sektionsformat, `[in diff]`/`[pre-existing]`-Labels,
  Gruppierung, `No tool findings.`-Zeile bei leerer Liste; System-Prompt enthält die zwei
  Grounding-Sätze.

## Doku

- Kurzer Abschnitt (EN, wie übrige `docs/`): **SAST/SCA grounding** — Vorbedingung (Tools im
  Container: `semgrep`, `trivy`; `dotnet` für opt-in), Beispiel-Config (`Naudit:Sast:*`),
  Hinweis Grounding-only (LLM entscheidet weiter), Sicherheits-/Latenz-Caveats (siehe unten).
- `CLAUDE.md`-Extension-Points um „**Neuer SAST-Analyzer:** `ISastAnalyzer` in
  `Infrastructure/Sast/` + Config-Eintrag" ergänzen.

## Bewusste Grenzen / Non-Goals & Caveats

- **`dotnet-sca` baut fremden Code** (`restore`/MSBuild ⇒ Code-Ausführung des zu reviewenden MRs).
  Semgrep/Trivy nicht (rein statisch). Daher `dotnet-sca` **opt-in**; im wegwerfbaren Workspace
  laufen lassen. Für self-hosted/interne Repos vertretbar — als Annahme dokumentiert.
- **Latenz:** Klon + (opt. Build) + mehrere Scanner ≈ mehrere Minuten zusätzlich (heute 70 s–3 min).
  Analyzer laufen parallel; Timeout pro Analyzer begrenzt den Worst Case.
- **Kein hartes Tool-Gate, kein LLM-Reduce-Default, keine WebSearch** (s. *Bewusste Abgrenzung*).
- **Container-Bereitstellung der Tools** (Dockerfile: `semgrep`/`trivy`/ggf. SDK) ist Teil der
  Umsetzung, aber als eigener Infrastruktur-Task — kein App-Logik-Thema.

## Verweise

- Zwischenfazit/Bericht (`nemotron-3-ultra:cloud`): Schwächen .NET-10-Halluzination, fehlende SCA,
  Recall-Varianz; Top-Empfehlung Grounding + Tool-Augmentation.
- Repo-Architektur & Extension-Points: `CLAUDE.md`
- Orchestrierung heute: `src/Naudit.Core/Review/ReviewService.cs`,
  `src/Naudit.Core/Review/PromtBuilder.cs`
- Wiederverwendete Prozess-Naht: `src/Naudit.Infrastructure/Ai/ClaudeCode/IProcessRunner.cs`
- Vorheriger Spec: `docs/superpowers/specs/2026-06-23-claudecode-cli-provider-design.md`
