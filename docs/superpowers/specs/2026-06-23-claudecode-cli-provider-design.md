# Design: ClaudeCode-Provider (CLI-Adapter)

*2026-06-23 · Projekt: Naudit*

## Ziel

Einen **vierten AI-Provider** ergänzen, der den Review nicht über ein SDK/HTTP-Endpoint, sondern
über die lokal installierte **`claude` CLI** (Claude Code, headless) laufen lässt. Motivation:
**Abo statt API-Key** — die CLI authentifiziert über den eingeloggten Claude-Account
(Pro/Max, via `CLAUDE_CODE_OAUTH_TOKEN` aus `claude setup-token`), statt pro Token über einen
Anthropic-API-Key zu zahlen.

Der Review bleibt **inhaltlich identisch** zu heute: **ein** Aufruf über das annotierte Diff,
JSON `{summary, verdict, comments[]}` zurück. Es ist ein reiner Provider-Tausch hinter der
bestehenden MEAI-Abstraktion — **kein** agentischer Review (kein Repo-Zugriff, keine Tools).

Deliverable dieser Iteration ist **nur der Adapter**. Dass `claude` auf dem ausführenden System
installiert und authentifiziert ist, ist eine **Umgebungs-Vorbedingung** (dokumentiert), nicht
Naudits Aufgabe — kein Dockerfile-/Deploy-Eingriff hier.

### Warum CLI und nicht das Agent SDK

Geprüft an der offiziellen Doku (`code.claude.com/docs/en/agent-sdk/overview`):

- Das **Agent SDK gibt es nur für Python und TypeScript** — kein .NET. Aus Naudit (.NET) heraus
  hieße „SDK nutzen" einen Python/TS-**Sidecar** betreiben; das SDK *entfernt* die Nicht-.NET-
  Runtime nicht, es *fügt eine hinzu*.
- Die SDK-Doku nennt als Auth ausschließlich `ANTHROPIC_API_KEY` (bzw. Bedrock/Vertex/Azure) und
  hält ausdrücklich fest, dass claude.ai-Login/Abo-Auth für auf dem SDK gebaute Produkte nicht
  vorgesehen ist. Der **Abo-/Headless-Pfad (`CLAUDE_CODE_OAUTH_TOKEN`) ist ein CLI-Feature** —
  passend für die eigene, selbst-gehostete Automatisierung auf eigenen Repos.
- Die SDK-Stärken (Tools, Sessions, MCP, Hooks, Subagents) zahlen erst bei einem **agentischen**
  Review — bewusst Non-Goal dieser Iteration.

→ Für „Abo statt Key" ist die CLI das passende Werkzeug; das SDK würde zum API-Key zurückdrängen
*und* einen Nicht-.NET-Dienst kosten.

## Entscheidungen

- **Form: `IChatClient`-Adapter** (Ansatz A). Ein neuer `ClaudeCodeChatClient : IChatClient` in
  `Infrastructure` kapselt den CLI-Aufruf; `AiClientFactory` bekommt einen `case`. **`Naudit.Core`
  bleibt unangetastet** und kennt weiter nur die MEAI-Abstraktion. Verworfen: eine *neue*
  Core-Abstraktion (`IReviewModel`) — bräche die Regel „Core nur an MEAI-Abstractions" und änderte
  Core ohne Mehrwert.
- **Auswahl config-only** über `Naudit:Ai:Provider` = `ClaudeCode`, exakt das Extension-Muster aus
  `CLAUDE.md`. `ReviewService`/`PromptBuilder` bleiben unverändert.
- **Headless-Single-Shot:** `claude -p --output-format json --max-turns 1`, **alle Tools aus**
  (reine Generierung, kein Agent-Loop, kein FS-Zugriff). Der Prompt geht über **stdin** an den
  Prozess (umgeht ARG_MAX bei großen Diffs).
- **System-Prompt überschreiben** (`--system-prompt`), nicht anhängen — wir wollen den reinen
  JSON-Reviewer, nicht Claude Codes Coding-Agent-Persona.
- **Neutrales Arbeitsverzeichnis** für den Prozess (z. B. ein Temp-Dir), damit `claude` keine
  ambient `CLAUDE.md`/Settings aus dem CWD einliest und den Prompt verfälscht.
- **Auth über Umgebung:** Das Kind erbt die Prozess-Umgebung; `CLAUDE_CODE_OAUTH_TOKEN` wird im
  Host gesetzt (dev/Coolify). Komfort: ist `AiOptions.ApiKey` gesetzt, reicht der Adapter ihn als
  `CLAUDE_CODE_OAUTH_TOKEN` in die Child-Env (Konfiguration via user-secrets `Naudit:Ai:ApiKey`).
- **Modell-Default `sonnet`,** wenn `AiOptions.Model` leer ist (CLI akzeptiert Aliasse wie
  `sonnet`/`opus` oder volle IDs). Sonst wie gesetzt nach `--model`.
- **Testbarkeit über eine dünne `IProcessRunner`-Naht** — analog zu `StubHttpMessageHandler`.
  Unit-Tests prüfen die gebauten Args/stdin und mappen ein kanonisches Envelope, ohne echtes
  `claude` aufzurufen.
- **Fail-closed:** jeder Fehlerfall wirft; **nie** ein Schein-Review. Konsistent mit dem heutigen
  „unparsebare Antwort → Exception".

## Komponenten

### 1. Prozess-Naht (`Naudit.Infrastructure/Ai/ClaudeCode`)

Eine schmale, testbare Abstraktion über den Subprozess (kein SDK):

```csharp
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct = default);
}

public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StdIn,
    IReadOnlyDictionary<string, string?>? Environment,   // additiv zur geerbten Env
    string? WorkingDirectory,
    TimeSpan Timeout);

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
```

- **`SystemProcessRunner`** (Default-Impl): `System.Diagnostics.Process` mit
  `RedirectStandardInput/Output/Error`, asynchronem Lesen, stdin schreiben+schließen,
  Timeout → `Kill(entireProcessTree: true)`, `CancellationToken`-Registrierung → Kill.
  `FileNotFound`/`Win32Exception` → klare Meldung „`claude` nicht auf PATH/installiert?".

### 2. `ClaudeCodeChatClient : IChatClient` (`Naudit.Infrastructure/Ai/ClaudeCode`)

- Ctor `(AiOptions options, IProcessRunner runner)` — Naht injizierbar für Tests.
- **`GetResponseAsync(messages, options, ct)`** baut den `ProcessSpec`:
  - System-`ChatMessage`(s) zusammengefügt → `--system-prompt <text>`.
  - User-`ChatMessage`(s) zusammengefügt → **`StdIn`** (das annotierte Diff).
  - Args: `-p`, `--output-format json`, `--max-turns 1`, Tools deaktiviert
    *(exakter Flag-Name versionsabhängig → ⚠️ CLI-Check, z. B. `--allowedTools ""`)*,
    `--model <Model|"sonnet">`.
  - `Environment`: `CLAUDE_CODE_OAUTH_TOKEN` = `options.ApiKey`, falls gesetzt (sonst geerbt).
  - `WorkingDirectory`: neutrales Temp-Dir. `Timeout` aus `options.TimeoutSeconds`.
  - Runner ausführen → **Envelope parsen** (`is_error`, `subtype == "success"`, `result`),
    `result`-Text defensiv von ```json-Fences befreien → `new ChatResponse(new ChatMessage(
    ChatRole.Assistant, text))`. Optional `Usage` aus dem Envelope mappen (nur fürs Logging).
- **`GetStreamingResponseAsync`**: dünner Wrapper, der das Einzelergebnis als einen Chunk yieldet
  (`ReviewService` nutzt nur die non-streaming Variante).
- **`GetService` / `Dispose`**: minimal.

Erwartetes Envelope (CLI `--output-format json`):
```json
{ "type": "result", "subtype": "success", "is_error": false,
  "result": "{\"verdict\":\"approve\",\"summary\":\"…\",\"comments\":[…]}",
  "session_id": "…", "total_cost_usd": 0.0, "usage": { … } }
```

### 3. Factory & Config (`Naudit.Infrastructure/Ai`)

- `AiOptions.AiProvider` um **`ClaudeCode`** erweitern.
- `AiClientFactory.Create`: neuer `case AiProvider.ClaudeCode → new ClaudeCodeChatClient(options,
  new SystemProcessRunner())`. **Kein** API-Key-Zwang (`RequireApiKey` entfällt hier — Auth läuft
  über die Umgebung).
- `AiOptions` sonst unverändert: `Model` (Default `sonnet` im Adapter), `TimeoutSeconds`
  wiederverwendet. **Kein** neuer Binary-Pfad-Parameter in dieser Iteration (PATH; nachrüstbar).
- `DependencyInjection.AddNauditInfrastructure` braucht **keine** Änderung (registriert `IChatClient`
  weiter über die Factory).

## Datenfluss

```
ReviewService.ReviewAsync (unverändert)
  → IChatClient = ClaudeCodeChatClient.GetResponseAsync(messages, JsonMode, ct)
      1) System-Messages   → --system-prompt
         User-Message(Diff)→ stdin
         Args: -p --output-format json --max-turns 1 --model … (Tools aus)
         Env:  CLAUDE_CODE_OAUTH_TOKEN (ApiKey override | geerbt)
         CWD:  neutrales Temp-Dir
      2) IProcessRunner.RunAsync(spec) ──▶ `claude` (Subprozess, Abo-Auth)
            ◀── stdout = { …, "result": "<JSON-String>", … }
      3) Envelope prüfen (is_error/subtype) → result extrahieren → Fences strippen
      4) return ChatResponse.Text = result
  → ReviewService parst result zu { summary, verdict, comments[] } (wie heute)
  → DiffParser-Validierung + PostReviewAsync (unverändert)
```

## Tests (TDD, spiegeln das bestehende Vorgehen)

- **`ClaudeCodeChatClientTests`** (mit Stub-`IProcessRunner`, kein echtes `claude`):
  - baut die korrekten Args (`-p`, `--output-format json`, `--max-turns 1`, `--model`, Tools-off)
    und übergibt die System-Message als `--system-prompt`, die User-Message als **stdin**;
  - Erfolgs-Envelope → `ChatResponse.Text == result`;
  - ```json-Fences im `result` werden gestrippt;
  - `is_error: true` / `subtype != "success"` / ExitCode ≠ 0 / leeres `result` → Exception;
  - Timeout (Runner meldet Timeout) → Exception;
  - `ApiKey` gesetzt → landet als `CLAUDE_CODE_OAUTH_TOKEN` in `ProcessSpec.Environment`.
- **`SystemProcessRunnerTests`** (optional, hermetisch): gegen ein triviales, portables Kommando
  (z. B. ein kleines Echo) Plumbing prüfen — stdin→stdout, ExitCode, Timeout-Kill — **ohne**
  `claude`. Bei Plattform-Flakiness als optionaler/Skip-Test.
- **`AiClientFactoryTests`** (falls vorhanden/sinnvoll): `Provider = ClaudeCode` →
  `ClaudeCodeChatClient`.

## Doku

- Kurzer Abschnitt (EN, wie die übrige `docs/`): **ClaudeCode provider** — Vorbedingung
  (`claude` installiert + `claude setup-token` → `CLAUDE_CODE_OAUTH_TOKEN`), Beispiel-Config
  (`Naudit:Ai:Provider=ClaudeCode`, `Naudit:Ai:Model=sonnet`), Hinweis „Abo statt API-Key" und der
  Non-Goal-Vermerk (kein agentischer Review, Binary muss lokal vorliegen).

## Bewusste Grenzen / Non-Goals

- **Kein Dockerfile/Node-im-Image, kein Sidecar.** `claude` ist Umgebungs-Vorbedingung; das
  Container-/Deploy-Thema ist ein **separater** späterer Schritt.
- **Kein agentischer Review** — kein Repo-Zugriff, keine Tools, kein MCP. Nur Diff-in/JSON-out.
- **Kein echtes Streaming** (dünner Wrapper), keine Sessions/Multi-Turn.
- **Kein Kosten-/Usage-Reporting** über Logging hinaus (Envelope liefert `total_cost_usd`; optional
  später auswerten).
- **Exakter „Tools-aus"-Flag-Name** ist versionsabhängig (⚠️ CLI-Check beim Implementieren), analog
  zu den MEAI-API-Checks im bestehenden Plan.

## Verweise

- Agent-SDK-Doku (Sprachen/Auth geprüft): `https://code.claude.com/docs/en/agent-sdk/overview`
- Repo-Architektur & Extension-Points: `CLAUDE.md`
- Bestehende Provider-Factory: `src/Naudit.Infrastructure/Ai/AiClientFactory.cs`
- Vorheriger Spec: `docs/superpowers/specs/2026-06-22-inline-comments-design.md`
- Board-Eintrag: `1. Projects/Naudit/Doings.md` → „Erweiterung: ClaudeCode-CLI-Provider"
