# Design: Autor-gebundene Claude-Sessions („Bring your own subscription")

*2026-07-11 · Projekt: Naudit*

## Ziel

Reviews sollen nicht mehr zwingend über **einen** zentralen AI-Account laufen. Nutzer
können ihre eigene Claude-Pro/Max-Session (Claude-Code-OAuth-Token aus
`claude setup-token`) in ihrem Naudit-Profil hinterlegen; Naudit nutzt diesen Token
**ausschließlich für Reviews von MRs/PRs, die der Nutzer selbst geöffnet hat**. So
verteilt sich die Usage auf die Beteiligten — jeder trägt die Last seiner eigenen
Reviews — und der global konfigurierte Provider bleibt als Fallback für alle übrigen
Fälle (kein Token, Bot-MRs, Rate-Limit) bestehen.

**Bewusst verworfen: Round-Robin-Pool.** Die Ursprungsidee („alle Tokens in einen
Topf, Reviews reihum verteilen") würde bedeuten, dass Alices Abo Bobs MR reviewt —
das ist Account-Sharing und verstößt gegen die Anthropic-Consumer-Terms
(Pro/Max-Abos sind personengebunden); die Konten der Beisteuernden riskieren
Sperrung. Das sanktionierte Muster (vgl. Claude Code GitHub Actions mit eigenem
`setup-token`-Token) ist: **der eigene Token automatisiert die eigene Arbeit.**
Genau darauf ist dieses Design festgelegt, und das Versprechen „dein Token wird nur
für deine eigenen MRs verwendet" ist Teil des UI und der Doku.

## Entscheidungen

- **Autor-gebundenes Routing, kein Pool.** Token eines Accounts ⇒ nur für MRs dieses
  Autors. Keine Verteilung fremder Last auf fremde Abos (ToS, s. o.).
- **Nur der Claude-Code-OAuth-Token als per-User-Credential.** Kein per-User
  API-Key, kein per-User Provider (wäre eine zweite Settings-Seite pro Nutzer —
  überdimensioniert). Die Naht (`IAiClientRouter`) ist aber so geschnitten, dass
  weitere Credential-Typen später nur eine weitere Router-Implementierung sind.
- **Fallback + Retry.** Kein Token hinterlegt ⇒ Review läuft wie heute über den
  globalen Provider. Scheitert ein Autor-Token-Lauf ⇒ Cooldown für diese Session
  plus **einmalige** Wiederholung über den globalen Client. Es geht praktisch nie
  ein Review verloren; fail-closed bleibt erhalten, wenn auch der globale Lauf
  scheitert.
- **Ausführung: In-Host-Subprozess mit Token pro Lauf** (bestehender
  `ClaudeCodeChatClient`, `ProcessSpec.Environment`). Verworfen: Container pro
  Session (Docker-Socket, Lifecycle-Management, komplexeres Deployment — ohne
  reales Problem zu lösen, da die CLI bei Env-Token-Auth stateless ist) und
  Remote-Runner beim Nutzer (neues Protokoll, fremde Infrastruktur — ein anderes
  Produkt).
- **Die `claude`-CLI kommt ins Runtime-Image.** Bisher „environment precondition"
  (siehe `docs/claudecode-provider.md`, Non-goals) — mit diesem Feature wird sie
  Kernfunktion, sonst ist das Feature im Container-Deployment nicht nutzbar.
- **Feature-Toggle, Default AUS:** `Naudit:Ai:AuthorSessions:Enabled` (DB-managed
  über den `SettingsCatalog`, wie die übrigen Settings). Der globale Provider
  bleibt frei wählbar (Anthropic-API, Ollama, …) — Autor-Läufe gehen immer über
  die CLI, unabhängig vom globalen Provider.

## Architektur

### Kernnaht: `IAiClientRouter` (Core)

`ReviewService` injiziert statt `IChatClient` eine neue Core-Abstraktion
(gleiches Muster wie `IPromptRedactor`/`IGitTokenProvider` — Interface in
`Naudit.Core.Abstractions`, Implementierungen in Infrastructure; Core-Regel
intakt, da nur MEAI-`IChatClient` und `ReviewRequest` referenziert werden):

```csharp
public interface IAiClientRouter
{
    Task<IChatClient> SelectAsync(ReviewRequest request, CancellationToken ct);
}
```

- **`SingleClientRouter`** (Default, Feature aus): gibt immer den global
  konfigurierten Client zurück — Verhalten exakt wie heute, bestehende Tests
  bleiben unverändert grün.
- **`AuthorSessionRouter`** (Feature an): löst den MR-Autor auf (über den
  `IAuthorLoginResolver`, s. u.) → aktiver Account mit hinterlegtem Token,
  `GitAuthorLogin`-Match und ohne laufenden Cooldown → baut einen
  per-Review-`ClaudeCodeChatClient` mit dem entschlüsselten Token und umhüllt
  ihn mit dem `FallbackChatClient` (unten). Kein Treffer in der Kette ⇒
  direkt der globale Client. Autor-Läufe nutzen ein eigenes Modell-Setting
  `Naudit:Ai:AuthorSessions:Model` (Default `sonnet`) — das globale
  `Naudit:Ai:Model` kann eine ID sein, die nur der globale Provider versteht
  (z. B. ein Ollama-Modellname), und taugt daher nicht als CLI-Modell.

`ReviewService.ReviewAsync` ruft `SelectAsync` einmal pro Review auf (vor dem
LLM-Call); alles andere im Review-Fluss bleibt unverändert.

### Autor-Ermittlung

- `ReviewRequest` erhält ein viertes Feld: `string? AuthorLogin`.
- **GitHub:** Der Webhook-Mapper füllt es aus `pull_request.user.login` — kein
  zusätzlicher API-Call.
- **GitLab:** Die MR-Webhook-Payload enthält nur `object_attributes.author_id`
  (numerisch), keinen Username. Deshalb ein Infrastruktur-Seam
  `IAuthorLoginResolver`: die GitHub-Implementierung gibt `request.AuthorLogin`
  durch; die GitLab-Implementierung macht **einen** API-Call
  (`GET /projects/:id/merge_requests/:iid` → `author.username`), nur wenn das
  Feature an ist und der Login fehlt. Fehlschlag der Auflösung ⇒ `null` ⇒
  globaler Client (fail-quiet, Review läuft normal).
- **`POST /review` (CI):** optionales Body-Feld `authorLogin` (die CI kennt den
  Autor, z. B. `CI_MERGE_REQUEST_AUTHOR`); fehlt es ⇒ globaler Client. Caller
  sind über das Shared-Secret ohnehin vertrauenswürdig.
- Matching Autor ↔ Account: case-insensitiv über die neue Spalte
  `GitAuthorLogin` (lowercased gespeichert, wie `GitHubLinkEntity.Login`). Nur
  Accounts mit `Status = Active` nehmen teil.

### Fallback & Session-Gesundheit

**`FallbackChatClient`** (Infrastructure, dünner `IChatClient`-Wrapper):

1. Autor-Client versuchen.
2. Bei **jeder** Exception: Cooldown für den Account setzen, Warnung loggen,
   dann **einmal** den globalen Client mit denselben Messages laufen lassen.
   Scheitert auch der ⇒ Review failt fail-closed wie heute.

Bewusster Trade-off: **keine** stderr-Klassifikation („war es wirklich ein
Rate-Limit?") — jeder Autor-Fehler gilt als Session-Problem. Worst Case bei
einem echten Prompt-Problem: ein zusätzlicher globaler Lauf, der dann genauso
scheitert — Verhalten wie heute, nur einen Versuch teurer. Dafür bleibt die
Logik trivial testbar.

**`SessionHealthRegistry`** (Singleton, in-memory, `accountId → coolUntil`):
bewusst **nicht** in der DB — nach einem Neustart vergessen, schlimmstenfalls
ein Fehlversuch mit erneutem Fallback. Cooldown-Dauer:
`Naudit:Ai:AuthorSessions:CooldownMinutes` (Default 30; Pro/Max-Rate-Limits
arbeiten in 5-h-Fenstern, 30 min ist ein pragmatischer Wiederanlauf-Takt).

**Prozess-Isolation:** Jeder CLI-Lauf bekommt zusätzlich zu
`CLAUDE_CODE_OAUTH_TOKEN` ein eigenes `CLAUDE_CONFIG_DIR` (Unterverzeichnis im
Scratch), damit parallele Läufe mit unterschiedlichen Tokens nie State teilen.
Das setzt der `ClaudeCodeChatClient` selbst (gilt damit auch für den globalen
ClaudeCode-Betrieb).

### Audit & Attribution

`ReviewEntity` erhält ein nullable `AiSessionAccountId`: „dieses Review lief
über die Session von X". Damit macht das Dashboard die Usage-Verteilung
sichtbar; die Token-Zählung selbst ist unverändert (der `ClaudeCodeChatClient`
mappt das CLI-Usage-Envelope bereits auf `ChatResponse.Usage`). Griff der
Fallback, wird das Review dem globalen Provider zugeschrieben
(`AiSessionAccountId = null`). Mechanik: das vom Router gebaute
Client-Gespann meldet, welcher Pfad tatsächlich geantwortet hat (z. B. ein
kleines Ergebnis-Objekt neben dem `IChatClient`); `ReviewService` reicht die
Account-Id wie Verdict/Usage an den `IReviewAuditSink` durch.

## Datenmodell

Eine Migration (hand-neutral gehalten wie gehabt: keine expliziten
Spaltentypen, SQLite-/Npgsql-Annotationen in `Up()`, Designer neutralisiert):

- `AccountEntity` + drei nullable Spalten:
  - `ClaudeSessionToken` — Data-Protection-verschlüsselt, Purpose
    `"Naudit.AiSessions"` (eigener Purpose, gleiche Mechanik wie
    Settings-Secrets). Write-only: der Klartext verlässt den Server nie wieder.
  - `ClaudeSessionUpdatedAtUtc`
  - `GitAuthorLogin` — lowercased; bei GitHub-Provider-Accounts beim
    Token-Speichern automatisch aus dem `Username` befüllt (überschreibbar);
    bei Local/OIDC-Accounts (GitLab-Betrieb) setzt der Nutzer ihn im Profil —
    ohne diesen Wert kann kein Review zugeordnet werden.
- `ReviewEntity` + nullable `AiSessionAccountId` (FK auf `AccountEntity`,
  `SetNull` beim Account-Löschen).

## API & Profil-UI (BFF-Muster wie gehabt)

- `GET /api/me/claude-session` → `{ configured, updatedAtUtc, coolingDownUntil,
  gitAuthorLogin }` — der Token selbst wird nie zurückgegeben.
- `PUT /api/me/claude-session` → Token setzen/ersetzen (+ optional
  `gitAuthorLogin`); leerer Token-String lässt den gespeicherten Token
  unangetastet (gleiche Blank-Semantik wie die Settings-Secrets).
- `DELETE /api/me/claude-session` → Token entfernen.
- `POST /api/me/claude-session/test` → Mini-CLI-Lauf („ping", kleines Modell)
  mit dem hinterlegten Token; Ergebnis „funktioniert / Fehler XY". Gleiche
  Semantik wie der Test-AI-Schritt im Setup-Wizard.
- Profilseite: neue Karte „Claude-Session" mit Status (hinterlegt seit,
  Cooldown bis), Setzen/Löschen, Test-Button, Kurzanleitung
  (`claude setup-token`) und dem ToS-Hinweis („wird ausschließlich für Reviews
  deiner eigenen MRs verwendet").
- Settings-Seite: `Naudit:Ai:AuthorSessions:Enabled` (Toggle),
  `CooldownMinutes` und `Model` (alle drei DB-managed via `SettingsCatalog`,
  Neustart-Banner wie bei allen Settings).

## Deployment

- Runtime-Stage des `Dockerfile`: Node LTS + `@anthropic-ai/claude-code`
  (Version gepinnt, wie die übrigen Pins). Image wächst um grob 150–250 MB;
  Trivy scannt Node mit (bestehende Gate-Regeln unverändert).
- Non-root bleibt: `CLAUDE_CONFIG_DIR` zeigt auf ein schreibbares Verzeichnis
  (Scratch), kein Schreiben ins Home nötig.
- Kein Orchestrator-/Compose-Änderungsbedarf; Coolify-Deployment unverändert.

## Testing

- **Router:** Autor→Account-Mapping (aktiv/inaktiv, kein Token, Cooldown,
  fehlender `GitAuthorLogin`), Feature-aus-Pfad ⇒ globaler Client,
  `POST /review` mit/ohne `authorLogin`.
- **`FallbackChatClient`:** Autor scheitert ⇒ globaler Client läuft und
  Cooldown gesetzt; beide scheitern ⇒ Exception (fail-closed).
- **`ClaudeCodeChatClient`:** `FakeProcessRunner` asserts
  `CLAUDE_CODE_OAUTH_TOKEN` und per-Lauf-`CLAUDE_CONFIG_DIR`.
- **GitLab-`IAuthorLoginResolver`:** `StubHttpMessageHandler` (URL + Mapping
  von `author.username`); Fehlerpfad ⇒ `null`.
- **GitHub-Webhook:** Mapping `pull_request.user.login` → `AuthorLogin`.
- **Endpoints:** `WebApplicationFactory` (401 ohne Session, write-only-Secret,
  Test-Lauf mit gefaktem Runner).
- E2E wie üblich manuell per Dogfooding.

## Nicht-Ziele

- **Kein Round-Robin-/Pool-Betrieb** über fremde Sessions (ToS, s. Ziel).
- Kein per-User AI-Provider oder per-User API-Key.
- Keine Container-Orchestrierung pro Session, kein Remote-Runner.
- Kein agentisches Review — der CLI-Lauf bleibt single-shot ohne Tools/Repo-Zugriff.
- Keine persistente Quota-/Cooldown-Buchhaltung und keine Fairness-Steuerung
  über den Autor-Bezug hinaus (der Autor-Bezug **ist** die Fairness).
