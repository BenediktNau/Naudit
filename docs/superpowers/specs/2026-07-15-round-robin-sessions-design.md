# Design: Round-Robin-Session-Routing

*2026-07-15 · Projekt: Naudit*

## Ziel

Aufbauend auf den Autor-Sessions (PR #53) ein **dritter Routing-Modus**, der die
hinterlegten Claude-Abos **rundlaufend** über die Reviews verteilt — statt jedes
Review autor-gebunden oder alles über den globalen Provider laufen zu lassen. So
trägt nicht ein einzelner Account die gesamte Usage; die Last verteilt sich über
aufeinanderfolgende Reviews auf alle Teilnehmer des Pools.

## Bewusste Entscheidung: ToS-Risiko

Round-Robin über **Consumer-Abos** (die `claude setup-token`-OAuth-Tokens) ist
**Account-Sharing** und verstößt gegen Anthropics Consumer-Terms — Alices Abo
reviewt Bobs PR. Das kann zur Sperrung der beteiligten Abos führen. Das Feature
wird auf **ausdrücklichen Wunsch** des Betreibers gebaut, mit offenem Risiko-Ja
(im Gegensatz zum autor-gebundenen Modell, das genau dieses Sharing vermeidet).
Zwei Konsequenzen sind daher fest im Design verankert:

1. **Informierte Zustimmung der Token-Geber.** Ein Token, den jemand für seine
   *eigenen* Reviews hinterlegt hat, wird **nicht** still zur Fremd-Nutzung
   umgewidmet. Pool-Teilnahme ist ein **separates, explizites Opt-in**.
2. **Sichtbarer ToS-Hinweis** an der Opt-in-Stelle und in der Doku.

## Entscheidungen (Brainstorming 2026-07-15)

- **Dritter Modus per Config-Enum** `Naudit:Ai:SessionRouting = Single | Author |
  RoundRobin` (Default `Single` = heutiges Verhalten). Löst den bool
  `Naudit:Ai:AuthorSessions:Enabled` ab (`Author` = altes „true"). Die geteilten
  Sub-Optionen `AuthorSessions:Model`/`CooldownMinutes` bleiben und werden von
  beiden Session-Modi genutzt.
- **Reine Rotation, ignoriert die Autorschaft** (Autor-Bevorzugung ist der
  `Author`-Modus — die drei Modi stehen orthogonal).
- **Sequentiell, keine Parallelität.** Der Webhook-Consumer
  (`ReviewBackgroundService`) bleibt ein einziger, der jedes `ReviewAsync` voll
  abwartet. Round-Robin verteilt die Abos **über die Zeit**, nicht gleichzeitig —
  kein Queue-Umbau, kein nebenläufiger Worker.
- **Explizites Opt-in pro Konto** (`AccountEntity.ShareSessionInPool`) — im Pool
  nur *aktiv + Token hinterlegt + Opt-in*.
- **Fallback per Reuse:** gewähltes Abo scheitert ⇒ Cooldown + ein Retry auf dem
  globalen Client (der bestehende `FallbackChatClient`, unverändert). Bewusst
  **nicht** „nächstes Pool-Abo durchprobieren" — die nächste Review rotiert ohnehin
  weiter; das spart eine neue Schleifen-Logik.
- **Kein quota-/lastgewichtetes Picken** (echtes Round-Robin = simple Rotation),
  **kein persistenter Rotationszustand** (In-Memory reicht).
- **Ein Branch, ein PR**, gestapelt auf `feat/author-sessions` (#53).

## Architektur

Wiederverwendet die komplette Autor-Session-Maschinerie aus PR #53; neu sind der
Router, das Opt-in-Flag und die Modus-Umschaltung.

### Modus-Wahl (Config + DI)

- **`Naudit:Ai:SessionRouting`** (Enum `SessionRouting { Single, Author, RoundRobin }`,
  Default `Single`). Ersetzt `AuthorSessionsOptions.Enabled`.
- `DependencyInjection.cs` schaltet 3-fach (heute: `if (authorSessions.Enabled)`):
  - `Single` ⇒ `SingleClientRouter` (wie heute).
  - `Author` ⇒ `AuthorSessionRouter` (wie heute).
  - `RoundRobin` ⇒ **`RoundRobinSessionRouter`** (neu, scoped).
- `SessionHealthRegistry` bleibt Singleton (von Author- und RoundRobin-Modus genutzt).

### RoundRobinSessionRouter (`src/Naudit.Infrastructure/Ai/ClaudeCode/`)

Implementiert `IAiClientRouter.SelectAsync(ReviewRequest, ct)` — Muster exakt wie
`AuthorSessionRouter`, nur die Kandidaten-Wahl unterscheidet sich:

1. **Pool laden:** aktive Konten mit `ShareSessionInPool == true` **und** gesetztem
   `ClaudeSessionToken`. Reihenfolge deterministisch (nach `AccountEntity.Id`).
2. **Cooldown-Filter:** Konten, die in der `SessionHealthRegistry` gerade auf
   Cooldown liegen, herausfiltern.
3. **Rotation:** aus der verbleibenden Liste das nächste per In-Memory-Cursor
   (`RoundRobinCursor`-Singleton, `Interlocked`-Zähler; `liste[cursor++ % count]`).
4. **Leerer Pool** (niemand opted-in / alle auf Cooldown) ⇒ globaler `IChatClient`,
   `AiClientSelection` mit null-Attribution (wie der Fall „kein Token" im Autor-Router).
5. **Happy Path:** Token entschlüsseln → per-Review `ClaudeCodeChatClient`
   (`AuthorSessions:Model`, eigenes `CLAUDE_CONFIG_DIR`) im `FallbackChatClient`
   → `AiClientSelection` mit `UsedSessionAccountId` = gewähltes Konto.

Der Router ist Infrastructure (wie `AuthorSessionRouter`); Core hält nur
`IAiClientRouter`/`AiClientSelection`. Core-Regel unberührt.

### Pool-Mitgliedschaft + Zustimmung

- **DB:** neue Spalte `AccountEntity.ShareSessionInPool` (bool, Default `false`) +
  **provider-neutrale Migration** (Muster wie die AuthorSessions-Migration in #53:
  keine expliziten Typen, `Up()` mit beiden Provider-Annotationen, Designer
  TYPE-frei, Snapshot SQLite-baked).
- **API:** `/api/me/claude-session` (aus #53) um das Flag erweitern — GET liefert
  `shareInPool` mit, PUT setzt es. Setzen nur sinnvoll, wenn ein Token hinterlegt
  ist (leerer Token + Opt-in ⇒ Konto ist schlicht nicht poolfähig, kein Fehler).
- **UI:** in der Profil-Claude-Session-Karte ein Schalter „**Share my Claude
  session for round-robin reviews of others' PRs**" mit sichtbarem
  **ToS-/Sperr-Risiko-Hinweis**; disabled, solange kein Token gesetzt ist.

### Attribution

Unverändert `ReviewEntity.AiSessionAccountId` (aus #53) — hält fest, welches
Pool-Konto ein Review getragen hat; im Dashboard sichtbar.

## Konfiguration

- **`Naudit:Ai:SessionRouting`** — Enum, Default `Single`. `SettingsCatalog`:
  ersetzt den Eintrag `Naudit:Ai:AuthorSessions:Enabled` durch
  `Naudit:Ai:SessionRouting` (non-secret); `AuthorSessions:Model`/`CooldownMinutes`
  bleiben.
- **Settings-UI:** die AI-Kategorie (aus #53) auf den 3-Wege-Enum umstellen
  (Dropdown Single/Author/RoundRobin statt Toggle); die Session-Sub-Felder
  (Model/Cooldown) für Author **und** RoundRobin einblenden.

## Fehlerbehandlung

- Token undekryptierbar (Keyring weg) ⇒ Konto überspringen (wie im Autor-Router),
  nicht crashen.
- Gewähltes Abo scheitert im Lauf ⇒ `FallbackChatClient`: Cooldown
  (`SessionHealthRegistry`) + ein globaler Retry. Kein Review geht verloren.
- Router-/DB-Fehler beim Pool-Laden ⇒ globaler Client (fail-open), nie Crash.

## Tests (TDD, bestehende Fakes + #53-Testmuster)

- **RoundRobinSessionRouter:** rotiert in Id-Reihenfolge über zwei/drei Pool-Konten
  (aufeinanderfolgende `SelectAsync` liefern verschiedene Konten) · überspringt ein
  Konto auf Cooldown · Nicht-Opt-in-Konto (Token, aber `ShareSessionInPool=false`)
  ist **nicht** im Pool · Konto ohne Token nicht im Pool · leerer Pool ⇒ globaler
  Client + null-Attribution · undekryptierbarer Token ⇒ übersprungen · Happy-Path
  setzt `UsedSessionAccountId` aufs gewählte Konto.
- **Opt-in-Flag:** Persistenz (Spalte) + `/api/me/claude-session` GET/PUT trägt
  `shareInPool` (write der Flag, read zurück).
- **DI-Schalter:** `SessionRouting = Single|Author|RoundRobin` registriert je den
  richtigen `IAiClientRouter` (drei Wiring-Tests).
- **Migration:** Round-trip SQLite (+ opt-in Postgres wie #53).

## Nicht-Ziele / Abgrenzung

- **Keine Parallelität / kein Durchsatz-Feature** — der Queue-Consumer bleibt
  sequentiell (eigene Design-Achse, bewusst ausgeklammert).
- **Kein** quota-/gewicht-basiertes Picken, **kein** persistenter Cursor,
  **kein** „nächstes Pool-Abo bei Fehler" (global-Retry reicht).
- **Keine** Umgehung des ToS-Risikos — es wird dokumentiert und per Opt-in
  eingegrenzt, nicht wegdesignt.

## Offene Enden / Sequencing

- **Basis ist `feat/author-sessions` (PR #53), nicht `main`.** Alle
  wiederverwendeten Bausteine (`IAiClientRouter`, `AuthorSessionRouter`,
  `ClaudeCodeChatClient`, `FallbackChatClient`, `SessionHealthRegistry`,
  `AccountEntity.ClaudeSessionToken`, `ReviewEntity.AiSessionAccountId`,
  `AuthorSessions`-Config) existieren nur dort. **Umsetzung erst nach/auf #53** —
  gestapelter PR (Muster wie #43 auf #42); bei Merge von #53 auf `main` rebasen.
- Der Wechsel `AuthorSessions:Enabled` (bool) → `SessionRouting` (enum) ist ein
  Config-Bruch. Da beide unveröffentlicht sind (POC, #53 noch offen), wird der bool
  sauber ersetzt statt beide Wege zu tragen; beim Deploy `Naudit__Ai__SessionRouting`
  statt `…AuthorSessions__Enabled` setzen.
