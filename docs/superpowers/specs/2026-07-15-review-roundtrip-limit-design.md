# Design: Review nur bei neuen Commits + Roundtrip-Limit pro PR

*2026-07-15 · Projekt: Naudit*

## Ziel

Naudit reviewt heute potenziell zu oft: GitLabs `update`-Action feuert auch bei
Label-/Beschreibungs-/Assignee-Änderungen, und es gibt keine Obergrenze, wie oft
derselbe MR/PR automatisch reviewt wird — jeder Push kostet einen vollen
LLM-Durchlauf. Zwei Scheiben:

1. **Nur bei neuen Commits reviewen** — Metadaten-Updates und Kommentare lösen
   nie ein Review aus.
2. **Max. Roundtrips pro PR** — nach N automatischen Reviews (Default **3**) ist
   Schluss; weitere Pushes werden übersprungen. Der synchrone CI-Trigger
   (`POST /review`) ist bewusst **ausgenommen** (Merge-Gate braucht immer ein
   frisches Verdict).

## Entscheidungen

- **Default 3, in den Settings änderbar** (`Naudit:Review:MaxRoundtrips`,
  `0` = unbegrenzt). Bewusster Bruch mit der „Default = heutiges Verhalten"-Konvention:
  das Limit ist eine Kostenbremse, die ab Werk greifen soll.
- **Der Audit-Log ist der Zähler.** Keine neue Tabelle, keine Migration: gezählt
  werden die vorhandenen `ReviewEntity`-Zeilen pro (Projekt, PR-Nummer). Der Zähler
  ist automatisch ehrlich — No-Op-Läufe (0 Changes ⇒ kein Post, kein Audit) zählen nie.
- **Skip im `ReviewService`, nicht im Webhook-Endpoint.** Der Endpoint bleibt dünn
  (validieren + enqueuen); der frühe Return im Service kostet weder Platform-Call
  noch Checkout noch LLM. CI ist über ein `Trigger`-Feld am `ReviewRequest` ausgenommen.
- **Hinweis im letzten erlaubten Review statt Kommentar beim ersten Skip.** Das
  N-te Review-Summary bekommt eine Schlusszeile („Roundtrip-Limit erreicht …");
  übersprungene Pushes danach sind still (nur Log). Damit braucht der
  „Hinweis nur einmal"-Zustand keine Persistenz — es gibt keinen Kommentar, der
  doppelt gepostet werden könnte.
- **Kein Reset bei Reopen.** Der Zähler ist die Lebenszeit-Summe des PR. Wer mehr
  braucht, hebt das Limit in den Settings oder stößt per CI-Trigger an.
- **Fail-open beim Zählerfehler.** DB weg ⇒ Count 0 ⇒ Review läuft — gleiche
  Best-Effort-Philosophie wie der Audit-Sink: das Review ist der Wert, das Limit
  nur die Bremse.
- **Ein Branch, ein PR** (`feat/review-roundtrip-limit`): beide Scheiben erfüllen
  gemeinsam „reviewe seltener".

## Scheibe A: Review nur bei neuen Commits

**GitLab** (`src/Naudit.Infrastructure/Git/GitLab/`):

- `GitLabMergeRequestAttributes` bekommt `[JsonPropertyName("oldrev")] string? OldRev`.
  GitLab schickt `oldrev` im `merge_request`-Event **nur**, wenn neue Commits
  gepusht wurden (es ist die vorherige HEAD-SHA).
- `GitLabWebhook.ToReviewRequest`: bei `action == "update"` zusätzlich
  `OldRev != null` verlangen, sonst `null` (kein Review). `open`/`reopen`
  bleiben unverändert (tragen nie `oldrev`, sollen aber reviewen).

**GitHub:** bereits korrekt — die Whitelist `opened`/`reopened`/`synchronize`
deckt genau „neu / wieder geöffnet / neue Commits" ab; Kommentare sind
`issue_comment`-Events und fallen schon am `eventType`-Filter raus. Nur ein
bestätigender Test (Nicht-Whitelist-Action ⇒ `null`), kein Codechange.

## Scheibe B: Roundtrip-Limit

### Seam (Core-Regel intakt)

```csharp
// Naudit.Core.Abstractions — Muster wie IAccessGate/IReviewAuditSink
public interface IReviewRoundtripCounter
{
    Task<int> CountAsync(string projectId, int mergeRequestIid, CancellationToken ct = default);
}
```

Implementierung `EfReviewRoundtripCounter` (`src/Naudit.Infrastructure/Ui/`,
neben `EfAccessGate`/`EfReviewAuditSink`):

```csharp
db.Reviews.CountAsync(r => r.Project.PlatformProjectId == projectId
                        && r.PrNumber == mergeRequestIid, ct)
```

Registrierung scoped in `DependencyInjection.cs` (DB ist immer an — keine Variante nötig).

### Trigger-Kennzeichnung

```csharp
public enum ReviewTrigger { Webhook, Ci }
// ReviewRequest += ReviewTrigger Trigger = ReviewTrigger.Webhook
```

Default `Webhook` ⇒ Webhook-Mapper und -Endpoints bleiben unverändert. Nur der
`POST /review`-Endpoint setzt `Trigger: ReviewTrigger.Ci`.

### Ablauf im `ReviewService`

Ganz vorne in `ReviewAsync`, **vor** `GetChangesAsync`:

```
wenn Trigger == Webhook und MaxRoundtrips > 0:
    count = SafeCount(projectId, iid)        // Exception ⇒ 0 (fail-open)
    wenn count >= MaxRoundtrips:
        return ReviewResult(leer, Approve, Skipped: true)
```

- `ReviewResult` bekommt `bool Skipped = false`; der `ReviewBackgroundService`
  loggt den Skip als Information (Core bleibt logger-frei).
- Bei `MaxRoundtrips == 0` wird der Counter gar nicht erst gefragt.
- Nach erfolgreichem Review: ist `count + 1 == MaxRoundtrips`, hängt
  `ComposeSummary` die Schlusszeile an (Zahlen dynamisch aus
  `count + 1`/`MaxRoundtrips`, hier mit Default 3):
  „_ℹ️ Roundtrip-Limit erreicht (3/3) — weitere Pushes an diesem PR werden nicht
  mehr automatisch reviewt._"
- Race zweier paralleler Reviews desselben PR kann den Hinweis im Extremfall
  doppelt oder gar nicht setzen — harmlos, bewusst nicht verriegelt.

## Konfiguration

- **`Naudit:Review:MaxRoundtrips`** — int, Default `3`, `0` = unbegrenzt.
  Landet in `ReviewOptions` (liest wie alles andere nur `IConfiguration`).
- `SettingsCatalog`-Eintrag (nicht secret) ⇒ in der Settings-UI editierbar
  (DB-Wert, Restart-Banner wie üblich); im Frontend in die Review-Kategorie
  der geführten Ansicht einsortiert.
- Doku: Abschnitt in `docs/configuration.md`, CLAUDE.md-Nachzug.

## Tests (TDD, bestehende Fakes reichen)

- **GitLab-Mapping:** `update` ohne `oldrev` ⇒ `null` · `update` mit `oldrev`
  ⇒ Request · `open`/`reopen` (ohne `oldrev`) ⇒ Request.
- **GitHub-Mapping:** Nicht-Whitelist-Action (z. B. `labeled`) ⇒ `null` (Bestätigung).
- **`ReviewService`:** Skip bei erreichtem Limit — `FakeGitPlatform`/`FakeChatClient`
  werden nicht angefasst, `Skipped == true` · `Trigger=Ci` reviewt trotz Limit ·
  `MaxRoundtrips=0` fragt den Counter nicht · Hinweiszeile genau im N-ten Summary,
  nicht in den Reviews davor · Counter-Exception ⇒ Review läuft (fail-open).
- **`EfReviewRoundtripCounter`:** zählt nur das richtige (Projekt, PR)-Paar
  (SQLite in-memory wie die anderen EF-Tests).

## Nicht-Ziele / Abgrenzung

- **Kein** manueller Re-Trigger-Kanal (`@naudit review`-Kommentar o. Ä.) — das
  gehört zum Review-Gedächtnis-/PR-Kommando-Feature (Spec 2026-07-09) und bleibt
  dort. Bis dahin ist der CI-Trigger der Weg, ein Review Nr. N+1 zu erzwingen.
- **Keine** Idempotenz/De-Dup wiederholter Webhook-Events (eigenes Planungs-Item).
- **Kein** per-Projekt-Limit — ein globaler Wert reicht dem POC; die Settings-Naht
  ließe eine spätere `.naudit.yml`-Override-Ebene zu.

## Offene Enden

- **Basis ist `main` (ohne PR #53).** Author-Sessions (#53) fügt `ReviewRequest`
  ebenfalls ein Feld hinzu (`AuthorLogin`) und erweitert den `ReviewService`-Kopf —
  je nach Merge-Reihenfolge ein trivialer Rebase an beiden Stellen.
