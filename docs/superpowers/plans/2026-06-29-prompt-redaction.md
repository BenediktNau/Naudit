# Prompt-Redaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Naudit maskiert Secrets/API-Keys/Passwörter, IP-Adressen und E-Mail-Adressen **bevor** der Diff (plus Grounding-Findings und MR-Titel) in den LLM-Prompt geht — deterministisch, line-preserving, in Core abstrahiert. Default an für alle Provider, abschaltbar via `Naudit:Redaction:Enabled=false`.

**Architecture:** Eine neue Core-Abstraktion `IPromptRedactor` (+ `NullPromptRedactor` als Identity) im Seam-Muster von `IFindingReducer`. Default-Implementierung `PatternRedactor` (Regex + konservative Shannon-Entropie) in `Naudit.Infrastructure/Redaction`. `ReviewService` schickt `CodeChange.Diff`, `ScanFinding.Message` und `ReviewRequest.Title` durch den Redactor, unmittelbar **vor** `PromptBuilder.Build`. `PromptBuilder` bleibt unverändert. Maskierung erfolgt **innerhalb** von Zeilen (Platzhalter `«redacted:<kind>»`), Diff-Strukturzeilen (`@@`/`+++`/`---`/`diff --git`/`index`) bleiben unangetastet ⇒ die New-File-Zeilennummerierung für Inline-Kommentare bleibt exakt erhalten.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, xUnit. Reine BCL (`System.Text.RegularExpressions`, `System.Math`) — **kein** externes Tool, **kein** Dockerfile-Eingriff.

## Global Constraints

- **Core-Regel:** `Naudit.Core` referenziert ausschließlich `Microsoft.Extensions.AI.Abstractions`. `IPromptRedactor` + `NullPromptRedactor` sind reine BCL/MEAI — erlaubt. Regex-/Entropie-Implementierung lebt in **Infrastructure**.
- **Solution-Datei ist `Naudit.slnx`** (nicht `.sln`). Build: `dotnet build Naudit.slnx`. Tests: `dotnet test Naudit.slnx`.
- **TDD:** red → green, **ein Commit pro Task**. Reine Interface-/Identity-Typen werden per Build + Nutzung in anderen Tests verifiziert.
- **Code-Kommentare auf Deutsch**; `README`/`docs/` auf Englisch.
- **Line-preserving:** der Redactor fügt/entfernt **nie** Zeilen und verändert Diff-Strukturzeilen nicht.
- **Sicherheit:** der rohe sensible Wert taucht **nie** im Redactor-Output auf (eigener Test, analog Gitleaks).
- **Fail-closed:** eine Exception im Redaction-Pass wird nicht geschluckt; Cancellation propagiert.
- **Rückwärtskompatibel auf Knopfdruck:** `Naudit:Redaction:Enabled=false` ⇒ `NullPromptRedactor` ⇒ exakt heutiges Verhalten.

## Pre-existing test note

`SastWiringTests.Disabled_registersNoAnalyzers_butReviewServiceResolves` ist auf dieser Maschine **vor** dieser Arbeit rot (resolved `ReviewService` ⇒ baut den GitLab-`HttpClient` ⇒ `new Uri("/")` bei leerer `GitLab:BaseUrl`, `DependencyInjection.cs:56`). Nicht Teil dieses Plans; **nicht anfassen** (evtl. in CI grün via Env-Var). Neues Wiring wird über `GetRequiredService<IPromptRedactor>()` getestet (braucht kein `IGitPlatform`).

## File Structure

**Neu (Core):**
- `src/Naudit.Core/Abstractions/IPromptRedactor.cs` — `IPromptRedactor` + `NullPromptRedactor`.

**Neu (Infrastructure):**
- `src/Naudit.Infrastructure/Redaction/RedactionOptions.cs`
- `src/Naudit.Infrastructure/Redaction/PatternRedactor.cs`

**Geändert:**
- `src/Naudit.Core/Review/ReviewService.cs` — Ctor +`IPromptRedactor`, Redaction-Pass vor `PromptBuilder.Build`.
- `src/Naudit.Infrastructure/DependencyInjection.cs` — `Naudit:Redaction` binden + `IPromptRedactor` registrieren.
- `src/Naudit.Web/appsettings.json` — `Naudit:Redaction`-Defaults.
- `docs/` (neuer Abschnitt), `CLAUDE.md` (Extension-Point + Request-flow).

**Neu (Tests):**
- `tests/Naudit.Tests/PatternRedactorTests.cs`
- `tests/Naudit.Tests/RedactionWiringTests.cs`
- `tests/Naudit.Tests/Fakes/FakePromptRedactor.cs`
- Geändert: `tests/Naudit.Tests/ReviewServiceTests.cs` (`CreateService` + neuer Test).

---

### Task 1: Core-Abstraktion `IPromptRedactor` + `NullPromptRedactor`

Reiner Seam-Typ; per Build verifiziert (Identity-Verhalten wird in Task 3 über `ReviewServiceTests` mitgenutzt).

**Files:**
- Create: `src/Naudit.Core/Abstractions/IPromptRedactor.cs`

**Interfaces:**
- Produces: `Naudit.Core.Abstractions.IPromptRedactor` mit `Task<string> RedactAsync(string text, CancellationToken ct = default)`; `NullPromptRedactor : IPromptRedactor` (Identity).

- [ ] **Step 1: Interface + Null-Impl anlegen**

```csharp
namespace Naudit.Core.Abstractions;

/// <summary>Maskiert sensible Werte (Secrets/Keys/Passwörter, IPs, E-Mails) in Text, der in den
/// LLM-Prompt geht. <b>Line-preserving:</b> fügt/entfernt nie Zeilen und lässt Diff-Strukturzeilen
/// unangetastet, damit die Zeilennummerierung für Inline-Kommentare erhalten bleibt.</summary>
public interface IPromptRedactor
{
    Task<string> RedactAsync(string text, CancellationToken ct = default);
}

/// <summary>No-Op-Redactor (Identity): liefert den Text unverändert. Aus-Fall der Redaction.</summary>
public sealed class NullPromptRedactor : IPromptRedactor
{
    public Task<string> RedactAsync(string text, CancellationToken ct = default) => Task.FromResult(text);
}
```

- [ ] **Step 2: Build grün** — `dotnet build Naudit.slnx`.
- [ ] **Step 3: Commit** — `feat(core): IPromptRedactor-Naht + NullPromptRedactor`.

---

### Task 2: `RedactionOptions` + `PatternRedactor` (TDD)

Der eigentliche Redactor. Erst Tests (red), dann Implementierung (green).

**Files:**
- Create: `tests/Naudit.Tests/PatternRedactorTests.cs`
- Create: `src/Naudit.Infrastructure/Redaction/RedactionOptions.cs`
- Create: `src/Naudit.Infrastructure/Redaction/PatternRedactor.cs`

**Interfaces:**
- `RedactionOptions { bool Enabled = true; double EntropyThreshold = 4.0; int MinEntropyTokenLength = 20; }`
- `PatternRedactor(RedactionOptions options) : IPromptRedactor`

- [ ] **Step 1 (RED): `PatternRedactorTests` schreiben**

Fälle (jeweils: Wert wird durch `«redacted:<kind>»` ersetzt, roher Wert **nicht** im Output):
- Private Key: ein PEM-Block (BEGIN/END + base64-Body) — Body-Zeilen maskiert, Zeilenanzahl gleich.
- Token: `AKIA…`, `ghp_…`, JWT `eyJ….….…` → `«redacted:token»`.
- Passwort-Zuweisung: `password = "hunter2"` → `password = «redacted:password»` (nur der Wert).
- IP: `10.0.4.12` und eine IPv6 → `«redacted:ip»`.
- E-Mail: `max.mustermann@firma.de` → `«redacted:email»`.
- Hoch-Entropie: ein 40-Zeichen-base64-Token → `«redacted:secret»`.
- **Precision:** eine normale Codezeile (`var sum = a + b;`) und ein kurzes Wort bleiben **unverändert**.
- **Line-preserving:** Input mit Diff-Strukturzeilen (`@@ -1,2 +1,3 @@`, `+++ b/f`, `--- a/f`) → diese Zeilen **unverändert**, Zeilenanzahl Input == Output.
- **Kein-Leak:** der rohe Secret-String ist als Substring **nicht** in `RedactAsync(...)`-Output enthalten.

Run: `dotnet test Naudit.slnx --filter PatternRedactorTests` → Expected: FAIL (Typ existiert noch nicht).

- [ ] **Step 2 (GREEN): `RedactionOptions` + `PatternRedactor` implementieren**

`RedactionOptions.cs` mit den drei Properties (Defaults oben).

`PatternRedactor.cs`:
- Statische, geordnete Regel-Liste `(string Kind, Regex Pattern)`: `private-key`-Trigger, Token-Pattern (AWS/GitHub/Slack/JWT), generische `password|secret|api_key|token|access_key`-Zuweisung (nur Wertgruppe ersetzen), IPv4 (mit Oktett-Check) + IPv6, E-Mail. `RegexOptions.Compiled | IgnoreCase` wo sinnvoll; **Timeout** je Regex (`MatchTimeout`) gegen ReDoS.
- `RedactAsync`: Input per `'\n'` splitten (Zeilenanzahl merken), **Strukturzeilen** (`@@`, `+++ `, `--- `, `diff --git`, `index `, `\ No newline`) unverändert durchreichen, sonst je Zeile: alle Pattern anwenden (Wert→`«redacted:<kind>»`), danach Entropie-Pass über verbleibende token-artige Substrings (`[A-Za-z0-9+/=_-]{MinEntropyTokenLength,}`, Shannon-Entropie ≥ `EntropyThreshold` ⇒ `«redacted:secret»`). Mit `'\n'` wieder zusammenfügen (gleiche Zeilenzahl).
- `ct.ThrowIfCancellationRequested()` am Anfang; keine Exception schlucken.
- Shannon-Entropie als private Helper (Häufigkeiten → `-Σ p·log2 p`).

Run: `dotnet test Naudit.slnx --filter PatternRedactorTests` → Expected: PASS.

- [ ] **Step 3: Commit** — `feat(redaction): PatternRedactor (Regex + Entropie), line-preserving`.

---

### Task 3: `ReviewService` Redaction-Pass (TDD)

`ReviewService` schickt Diff + Finding-Message + Titel durch den injizierten Redactor.

**Files:**
- Create: `tests/Naudit.Tests/Fakes/FakePromptRedactor.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs`

- [ ] **Step 1 (RED): Fake + Test**

`FakePromptRedactor` — ersetzt jeden Vorkommnis eines Sentinels (z. B. `"SECRET"`) durch `"«red»"`; zählt Aufrufe.

In `ReviewServiceTests`: `CreateService` um Parameter `IPromptRedactor? redactor = null` erweitern → an den Ctor durchreichen (`redactor ?? new NullPromptRedactor()`). Neuer Test:

```csharp
[Fact]
public async Task ReviewAsync_redactsDiff_findingMessage_andTitle_beforePrompt()
{
    var chat = new FakeChatClient("""{"summary":"ok","verdict":"approve"}""");
    var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ +1 @@\n+var k = \"SECRET\";")]);
    var finding = new ScanFinding("trivy", FindingCategory.Sca, FindingSeverity.High, "leak SECRET here", "R", "a.cs", 1);
    var analyzers = new[] { new FakeSastAnalyzer("trivy", new[] { finding }) };
    var service = CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
        analyzers, redactor: new FakePromptRedactor("SECRET"));

    await service.ReviewAsync(new ReviewRequest("1", 42, "Fix SECRET in config"));

    var userText = chat.LastMessages![1].Text!;
    Assert.DoesNotContain("SECRET", userText);   // Diff + Finding-Message + Titel redigiert
    Assert.Contains("«red»", userText);
}
```

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests` → Expected: FAIL (Ctor-Param fehlt / nicht redigiert).

- [ ] **Step 2 (GREEN): `ReviewService` erweitern**

- Ctor: `IPromptRedactor redactor` als letzten Parameter ergänzen.
- In `ReviewAsync`, **nach** `CollectFindingsAsync` und **vor** `PromptBuilder.Build`:

```csharp
// Redaction: Secrets/IPs/PII maskieren, bevor irgendetwas das LLM erreicht.
var redChanges = new List<CodeChange>(changes.Count);
foreach (var c in changes)
    redChanges.Add(c with { Diff = await redactor.RedactAsync(c.Diff, ct) });

var redFindings = new List<ScanFinding>(findings.Count);
foreach (var f in findings)
    redFindings.Add(f with { Message = await redactor.RedactAsync(f.Message, ct) });

var redRequest = request with { Title = await redactor.RedactAsync(request.Title, ct) };

var messages = PromptBuilder.Build(options.SystemPrompt, redRequest, redChanges, redFindings);
```

Run: `dotnet test Naudit.slnx --filter ReviewServiceTests` → Expected: PASS (inkl. aller Bestands-Tests via `NullPromptRedactor`).

- [ ] **Step 3: Commit** — `feat(core): Redaction-Pass in ReviewService (Diff/Finding/Titel)`.

---

### Task 4: DI-Wiring (TDD)

**Files:**
- Create: `tests/Naudit.Tests/RedactionWiringTests.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`

- [ ] **Step 1 (RED): `RedactionWiringTests`**

Baut den Provider wie `SastWiringTests.Build` (in-memory config + `AddNauditInfrastructure`), resolved aber **nur** `IPromptRedactor`:
- Section fehlt ⇒ `PatternRedactor` (Default an).
- `Naudit:Redaction:Enabled=true` ⇒ `PatternRedactor`.
- `Naudit:Redaction:Enabled=false` ⇒ `NullPromptRedactor`.

Run: `dotnet test Naudit.slnx --filter RedactionWiringTests` → Expected: FAIL.

- [ ] **Step 2 (GREEN): Registrierung in `AddNauditInfrastructure`**

```csharp
var redactionOptions = configuration.GetSection("Naudit:Redaction").Get<RedactionOptions>() ?? new RedactionOptions();
services.AddSingleton<IPromptRedactor>(redactionOptions.Enabled
    ? new PatternRedactor(redactionOptions)
    : new NullPromptRedactor());
```

(`using Naudit.Infrastructure.Redaction;` ergänzen.)

Run: `dotnet test Naudit.slnx --filter RedactionWiringTests` → Expected: PASS.

- [ ] **Step 3: Commit** — `feat(infra): IPromptRedactor-Wiring (Naudit:Redaction, Default an)`.

---

### Task 5: Doku + Config-Defaults

**Files:**
- Modify: `src/Naudit.Web/appsettings.json` — `Naudit:Redaction` Block (`Enabled: true`, `EntropyThreshold: 4.0`, `MinEntropyTokenLength: 20`).
- Modify: `docs/` — neuer Abschnitt **Prompt redaction** (EN): was maskiert wird, Default an, Config, Trade-off, Presidio/Namen später.
- Modify: `CLAUDE.md` — Extension-Point „**Neuer Redactor:** `IPromptRedactor` in `Infrastructure/Redaction/`" + Request-flow-Notiz „Redaction-Pass vor `PromptBuilder`".

- [ ] **Step 1:** appsettings + docs + CLAUDE.md schreiben.
- [ ] **Step 2:** `dotnet build Naudit.slnx` grün (JSON valide).
- [ ] **Step 3: Commit** — `docs(redaction): Prompt-Redaction dokumentieren + appsettings-Defaults`.

---

### Task 6: Final — volle Suite + Self-Review

- [ ] **Step 1:** `dotnet test Naudit.slnx` — alle grün **außer** dem bekannten, fremden `SastWiringTests`-Fehler (s. o.). Keine **neuen** Fehler.
- [ ] **Step 2:** Self-Review-Pass über das Diff (Core-Regel gewahrt? line-preserving? kein Leak? Cancellation?).
- [ ] **Step 3:** Vault-Notiz + Board aktualisieren (Doing→Completed), PR öffnen.

## Verweise

- Design-Spec: `docs/superpowers/specs/2026-06-29-prompt-redaction-design.md`
- Prompt-Aufbau: `src/Naudit.Core/Review/PromtBuilder.cs`; Orchestrierung: `src/Naudit.Core/Review/ReviewService.cs`
- Seam-Vorbild: `IFindingReducer` / `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs`
- Architektur & Extension-Points: `CLAUDE.md`
