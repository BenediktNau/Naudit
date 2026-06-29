# Design: Prompt-Redaction — Secrets/PII vor dem LLM-Context maskieren

*2026-06-29 · Projekt: Naudit*

## Ziel

Naudit schickt heute den **gesamten Diff** ungefiltert an das LLM (die Architektur-Notiz führt
„ganzer Diff geht ans LLM" ausdrücklich als bekannte Lücke). Bei Cloud-Providern (Anthropic,
OpenAI-kompatibel z. B. NVIDIA, ClaudeCode-CLI) verlässt damit potenziell sensibler Inhalt —
hartkodierte Secrets, API-Keys, Passwörter, interne IP-Adressen/Hosts, personenbezogene Daten —
die eigene Infrastruktur. Für einen **Security**-Review-Bot ist das eine glaubwürdigkeitsrelevante
Lücke.

Diese Iteration führt einen **Redaction-Pass** ein, der solche Werte maskiert, **bevor** der Prompt
gebaut und gesendet wird — deterministisch, in Core, **ohne die Diff-Struktur zu zerstören** (die
Zeilennummerierung für Inline-Kommentare muss exakt erhalten bleiben).

**Scope dieser Iteration** (gemeinsam festgelegt):
- **Maskiert:** High-Signal-Secrets (PEM/Private Keys, API-Keys/Tokens à la AWS/GitHub/JWT/Slack,
  `password=`/`secret=`/`api_key=`-Zuweisungen, hoch-entropische Token), **Netzwerk-Identifikatoren**
  (IPv4/IPv6) und **PII in Form von E-Mail-Adressen**.
- **Default an, für alle Provider**, abschaltbar via `Naudit:Redaction:Enabled=false`.

### Bewusste Abgrenzung

- **Namen / breite PII via NER/Presidio:** NICHT in dieser Iteration. Presidio ist ein externer
  Python-Dienst → eigene Infrastruktur-Komponente; hinter derselben `IPromptRedactor`-Naht später
  nachrüstbar (`PresidioRedactor`/`LlmRedactor`). E-Mails werden rein per Regex erfasst.
- **Redaction ist kein Ersatz für Gitleaks.** Gitleaks (Sensor) meldet *dass/wo* ein Secret liegt
  (Grounding); Redaction sorgt dafür, dass der *Wert* das LLM nicht erreicht. Beide ergänzen sich,
  und Redaction läuft **unabhängig** vom SAST-Feature (auch bei `Naudit:Sast:Enabled=false`).
- **Kein Re-Scan des geklonten Workspace.** Redaction arbeitet auf dem **Prompt-Text** (Diff,
  Finding-Messages, MR-Titel), nicht auf dem Checkout — die Garantie hängt nicht an SAST/Checkout.
- **Kein hartes Gate aus der Redaction.** Sie verändert nur den Prompt-Input; Verdict-Logik bleibt
  unangetastet.

## Entscheidungen

- **Neue Core-Abstraktion `IPromptRedactor`** (`Naudit.Core.Abstractions`), exakt das Plugin-/Seam-
  Muster von `IFindingReducer`: Default-Implementierung jetzt deterministisch (Regex + konservative
  Entropie), später config-/seam-tauschbar (Presidio/LLM). **Core bleibt MEAI-only** — Regex
  (`System.Text.RegularExpressions`) und Entropie-Mathematik sind BCL, kein SDK.
- **Async-Signatur** `Task<string> RedactAsync(string text, CancellationToken ct = default)` —
  CPU-only-Default braucht es nicht, aber so passt die Naht zu `IFindingReducer` und ein künftiger
  LLM-/Presidio-Redactor (I/O) tritt ohne Vertragsänderung an dieselbe Stelle.
- **Default-Implementierung `PatternRedactor` in `Infrastructure/Redaction/`** (konsistent mit
  „Default-Impl in Infrastructure", vgl. `DeterministicFindingReducer`). **`NullPromptRedactor`
  (Identity) liegt in Core** als triviale, abhängigkeitsfreie Vorgabe — nutzbar für den Aus-Fall in
  der DI **und** als Default in Core-Tests.
- **Line-preserving Redaction.** Ersetzt nur **innerhalb** von Zeilen (Platzhalter
  `«redacted:<kind>»`), fügt/entfernt **nie** Zeilen und tastet Diff-Struktur-/Metazeilen (`@@`,
  `+++ `, `--- `, `diff --git`, `index `, `\ No newline`) **nicht** an. Damit bleibt die
  New-File-Zeilennummerierung in `PromptBuilder.AppendAnnotatedDiff` exakt erhalten — kritisch für
  die Inline-Kommentar-Positionen. *Verifiziert:* GitLab `changes[].diff` und GitHub `files[].patch`
  liefern Hunks ab `@@` (keine `index`-Hashes); die Strukturregel ist defensiv.
- **Typisierte Platzhalter** (`«redacted:private-key|token|password|secret|ip|email»`): erhalten
  Lesbarkeit/Struktur und signalisieren dem LLM „hier stand ein Secret" → das hilft sogar, **hart-
  kodierte Secrets zu flaggen** (Synergie statt reinem Qualitätsverlust). Die spitzen Klammern
  `«»` sind im Quellcode selten und damit kollisionsarm.
- **Anwendungsumfang im Prompt:** `change.Diff` (alle Changes), `ScanFinding.Message` (andere Tools
  könnten Snippets in der Message führen; Gitleaks lässt den rohen Wert ohnehin weg) und
  `request.Title`. Die **LLM-Antwort** wird *nicht* redaktiert — das Modell hat den Wert nie gesehen.
- **Anwendungsstelle = `ReviewService`,** unmittelbar vor `PromptBuilder.Build` (heute Zeile 30).
  Der Redactor wird injiziert (immer vorhanden: `Null`- oder `PatternRedactor`), analog zu
  `IFindingReducer`. **`PromptBuilder` bleibt unverändert.**
- **Konservative Heuristik (Precision vor Recall bei Entropie).** Strukturierte Pattern zuerst;
  die Entropie-Regel greift nur auf **token-artige** Substrings ab Mindestlänge und über Schwellwert,
  um base64-Blobs/Hashes/Minified/UUIDs **nicht** flächig zu schwärzen. Schwellen per Config
  justierbar. *Trade-off bewusst:* lieber ein Secret über ein Pattern fassen als halbe Codezeilen
  unkenntlich machen.
- **Default an (alle Provider)** — bewusst entgegen der sonst opt-in-Konvention (vgl. SAST
  `Enabled=false`). Safe-by-default für ein Privacy-Feature (so entschieden). **Das ist eine
  Verhaltensänderung** gegenüber heute und wird in Doku/Release-Notes hervorgehoben. Abschalten:
  `Naudit:Redaction:Enabled=false` ⇒ `NullPromptRedactor` ⇒ exakt heutiges Verhalten.
- **Fail-closed.** Der `PatternRedactor` ist I/O-frei und kann praktisch nicht fehlschlagen; tritt
  dennoch eine Exception auf, wird sie **nicht geschluckt** → der Review schlägt fehl, statt
  ungeschützt zu senden (lieber kein Review als ein Leak). Cancellation propagiert.

## Komponenten

### 1. Core-Abstraktion `IPromptRedactor` + `NullPromptRedactor` (`Naudit.Core/Abstractions`)

```csharp
namespace Naudit.Core.Abstractions;

/// <summary>Maskiert sensible Werte (Secrets/Keys/Passwörter, IPs, E-Mails) in Text, der in den
/// LLM-Prompt geht. <b>Line-preserving:</b> fügt/entfernt nie Zeilen, lässt Diff-Strukturzeilen
/// unangetastet — damit bleibt die Zeilennummerierung für Inline-Kommentare erhalten.</summary>
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

### 2. `PatternRedactor` (`Infrastructure/Redaction/PatternRedactor.cs`)

- Hält eine geordnete Liste benannter Regeln `(string Kind, Regex Pattern)` plus eine
  Entropie-Regel. Verarbeitet den Input **zeilenweise**: Strukturzeilen werden durchgereicht,
  alle anderen Zeilen intra-line maskiert (Reihenfolge: spezifische Pattern → generische
  Zuweisungen → IP/E-Mail → Entropie). Matches werden durch `«redacted:<kind>»` ersetzt.
- Erhält `RedactionOptions` (Schwellen). `Name => "pattern"`.
- **Sicherheits-Kernpunkt (analog Gitleaks):** der rohe Wert taucht **nie** im Output auf — ein
  Test belegt, dass der Platzhalter den Match vollständig ersetzt.

**Default-Pattern-Katalog (Kind → Erkennung):**

| Kind | Erkennung (Beispiele) |
|------|------------------------|
| `private-key` | base64-Body-Zeilen eines PEM-Blocks (`-----BEGIN … PRIVATE KEY-----` als Trigger; lange base64-Zeilen) |
| `token` | `AKIA[0-9A-Z]{16}`, `ghp_[A-Za-z0-9]{36}`, `github_pat_…`, `xox[baprs]-…`, JWT `eyJ…\.…\.…` |
| `password` | `(?i)(password|passwd|pwd|secret|api[_-]?key|access[_-]?key|token)\s*[:=]\s*["']?<wert>["']?` → **nur der Wert** wird maskiert |
| `ip` | IPv4 (mit Oktett-Validierung) und IPv6 |
| `email` | `[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}` |
| `secret` | Entropie-Fallback: token-artige Substrings ab `MinEntropyTokenLength`, Shannon-Entropie ≥ `EntropyThreshold` |

### 3. `RedactionOptions` (`Infrastructure/Redaction/RedactionOptions.cs`)

```
Naudit:Redaction:Enabled              = true     # Default AN; false ⇒ NullPromptRedactor (heutiges Verhalten)
Naudit:Redaction:EntropyThreshold     = 4.0      # Shannon-Bits/Zeichen für den Entropie-Fallback
Naudit:Redaction:MinEntropyTokenLength = 20      # nur Substrings ab dieser Länge prüft der Entropie-Pass
```

### 4. DI-Komposition (`DependencyInjection.cs`)

`redactionOptions` aus `Naudit:Redaction` binden; **`Enabled` Default `true`** (Section fehlt ⇒
`new RedactionOptions()` mit `Enabled=true`). Dann:

```csharp
services.AddSingleton<IPromptRedactor>(redactionOptions.Enabled
    ? new PatternRedactor(redactionOptions)
    : new NullPromptRedactor());
```

Provider-unabhängig (vom Nutzer so gewählt: „immer an, alle Provider"). Kein Zugriff auf
`AiProvider` nötig ⇒ Core-Regel unberührt.

### 5. `ReviewService` (Erweiterung)

Ctor wächst um `IPromptRedactor redactor`. Direkt vor dem Prompt-Aufbau:

```
changes  = GetChangesAsync(...)                      // wie heute
findings = CollectFindingsAsync(...)                 // wie heute
// NEU: Redaction-Pass vor dem Prompt
redChanges  = changes  → CodeChange mit RedactAsync(Diff)
redFindings = findings → ScanFinding mit RedactAsync(Message)
redTitle    = RedactAsync(request.Title)             // request mit redigiertem Titel an Build
messages = PromptBuilder.Build(SystemPrompt, request', redChanges, redFindings)
```

`PromptBuilder` unverändert.

## Datenfluss

```
ReviewService.ReviewAsync
  → IGitPlatform.GetChangesAsync ........... Diff (wie heute)
  → CollectFindingsAsync ................... ScanFinding[] Grounding (wie heute)
  → IPromptRedactor.RedactAsync ............ Diff + Finding.Message + Title  (Secrets/IP/Email → «redacted:…»)
  → PromptBuilder.Build .................... (unverändert)
  → IChatClient.GetResponseAsync(JsonMode) . { summary, verdict, comments } (wie heute)
  → Verdict-Mapping · Inline/Orphans · PostReviewAsync · ReviewResult (wie heute)
```

Nur ein zusätzlicher, idempotenter Transformationsschritt zwischen Sammlung und Prompt-Aufbau.

## Tests (TDD, spiegeln das bestehende Vorgehen)

- **`PatternRedactorTests`:** je Kind ein Fall (Private Key, AWS/GitHub-Token, JWT,
  `password=`-Zuweisung, IPv4/IPv6, E-Mail, Hoch-Entropie-Token) wird durch `«redacted:<kind>»`
  ersetzt; **Zeilenanzahl bleibt erhalten**; Diff-Strukturzeilen (`@@`/`+++`/`---`) bleiben
  unangetastet; eine normale Codezeile / kurze niedrig-entropische Strings werden **nicht**
  geschwärzt (Precision); **roher Wert nie im Output** (eigener, expliziter Test).
- **`RedactionWiringTests`:** `Enabled=true` (oder Section fehlt) ⇒ `GetRequiredService<IPromptRedactor>()`
  ist ein `PatternRedactor`; `Enabled=false` ⇒ `NullPromptRedactor`. (Resolved **nur** den Redactor,
  nicht `IGitPlatform` — entkoppelt vom GitLab-BaseUrl-Wiring.)
- **`ReviewServiceTests` (erweitert):** mit `FakePromptRedactor` (ersetzt ein Sentinel) wird
  bewiesen, dass `ReviewService` Diff **und** Finding-Message **und** Titel durch den Redactor
  schickt und der redigierte Text im Prompt landet (über `FakeChatClient`, der die Messages
  einfängt). No-Op-Pfad (`NullPromptRedactor`) ⇒ identischer Prompt wie heute.
- **Fake:** `tests/Naudit.Tests/Fakes/FakePromptRedactor.cs`.

## Doku

- `docs/` neuer Abschnitt **Prompt redaction** (EN): was maskiert wird, **Default an**,
  `Naudit:Redaction:*`, Qualitäts-Trade-off, Hinweis dass Namen/breite PII (Presidio) später kommen.
- `CLAUDE.md`: Extension-Point „**Neuer Redactor:** `IPromptRedactor` in `Infrastructure/Redaction/`
  + Config-Eintrag, kein Core-Eingriff"; Request-flow-Notiz „Redaction-Pass vor `PromptBuilder`".
- `appsettings.json`: `Naudit:Redaction`-Defaults.

## Bewusste Grenzen / Caveats

- **Heuristik ⇒ False Negatives** (unübliche Secret-Formen entgehen) **und False Positives**
  (vierteilige Versionsnummern wie `10.0.19041.1` als IP, lange Hashes als „secret"). Precision-
  orientiert getunt und per Config justierbar; ein restloses Erfassen ist nicht das Ziel.
- **Qualitäts-Trade-off:** ein maskierter Wert ist weniger Kontext fürs LLM; durch typisierte
  Platzhalter abgemildert (das Modell weiß weiterhin „hier war ein Token/eine IP").
- **Multi-line Secrets (PEM):** werden über die Entropie-/Marker-Regel **pro Zeile** erfasst, nicht
  als zusammenhängender Block — Zeilenerhalt hat Vorrang.
- **Namen/breite PII:** out of scope (Presidio später, gleiche Naht).
- **Default-Verhaltensänderung:** ab dieser Version wird per Default redaktiert — in Release-Notes
  klar kommunizieren.

## Verweise

- Architektur-Lücke „ganzer Diff geht ans LLM": `BenediktsMind/1. Projects/Naudit/Naudit – Architektur.md`
- Prompt-Aufbau: `src/Naudit.Core/Review/PromtBuilder.cs`; Orchestrierung: `src/Naudit.Core/Review/ReviewService.cs` (Zeile 30)
- Seam-Vorbild: `IFindingReducer` / `src/Naudit.Infrastructure/Sast/DeterministicFindingReducer.cs`
- Ergänzender Secrets-Sensor: `src/Naudit.Infrastructure/Sast/GitleaksAnalyzer.cs` (rohen Wert nie übernehmen)
- Tool-Hintergrund (Presidio/Secrets-Tools): `BenediktsMind/1. Projects/Bachelorarbeit/2026-06-18 CodeRabbit – SAST-Tools + KI-Pipeline.md`
- Board-Eintrag: `1. Projects/Naudit/Doings.md` → „Secrets IPs Keys Passwords … nicht in den Context"
