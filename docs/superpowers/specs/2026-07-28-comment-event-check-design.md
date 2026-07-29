# Design: Prüfung des Kommentar-Events beim Start

*2026-07-28 · Projekt: Naudit*

## Ziel

Die Antwort-Kommandos (`@naudit fp` / `@naudit ok`, siehe
[Review-Gedächtnis](../../review-memory.md)) hängen daran, dass die Plattform Naudit die
Antwort überhaupt zustellt — GitHub über die Ereignisart `pull_request_review_comment`,
GitLab über den Note-Hook (`note_events`, in der Oberfläche der Trigger „Comments").

Fehlt das Abonnement, fällt das Feature **völlig still** aus: kein Fehler, keine
Log-Zeile, keine Antwort im Thread. Für den Nutzer sieht es aus, als ignoriere Naudit
ihn; die False-Positive-Einträge im Projekt-Gedächtnis und die Resolution-Signale der
Auswertung bleiben einfach aus.

Der Einrichtungs-Wizard abonniert beides seit Einführung des Kommandos
(`GitHubManifest.Build` setzt `["pull_request", "pull_request_review_comment"]`,
`GitLabHookCreator` setzt `note_events = true`). Das Problem trifft **bestehende**
Installationen: GitHub rüstet die Ereignisliste einer schon angelegten App nie
nachträglich nach, und ein von Hand nach der alten Dokumentation eingerichteter Hook
hat den Trigger nie bekommen.

Ziel: Naudit erkennt diese Lücke selbst und schreibt beim Start eine Warnung mit der
konkreten Handlungsanweisung ins Log.

## Entscheidungen

- **Nur prüfen, nicht reparieren.** Bei GitHub geht es technisch nicht anders — die
  Ereignisliste einer App ist Teil ihrer Definition und über die REST-API nicht
  änderbar, nur im Browser oder beim Anlegen per Manifest. Bei GitLab wäre ein `PUT`
  auf den Hook möglich; darauf wird bewusst verzichtet, damit beide Plattformen sich
  gleich verhalten und Naudit keine Konfiguration hinter dem Rücken des Betreibers
  ändert.
- **Log statt WebUI.** Adressat ist der Betreiber, nicht der Review-Nutzer. Kein neuer
  Endpunkt, keine SPA-Änderung.
- **Einmal nach dem Start, nicht bei jedem Review.** Die Ereignisliste ändert sich
  praktisch nie; eine Prüfung pro Hostlauf reicht. Nach einem Settings-Restart läuft
  sie erneut.
- **Nicht in `StartupReport`.** Der Konfigurationsblock aus PR #79 ist bewusst eine
  reine, synchrone Funktion ohne Netzwerkzugriff. Diese Prüfung braucht HTTP und gehört deshalb in einen
  eigenen Hintergrunddienst — nicht in `StartAsync`, damit ein hängender API-Aufruf den
  Hoststart nicht blockiert.
- **Fail-quiet, aber nicht blind.** Ein API-Fehler erzeugt **keine** Warnung. Wer sich
  an Fehlalarme gewöhnt, übersieht den echten Fall. Nur ein nachgewiesenes Fehlen des
  Events warnt.

## Komponenten

### Die Naht

```csharp
namespace Naudit.Infrastructure.Setup;

/// <summary>Prüft, ob die Plattform-Seite Naudit die Antworten auf Inline-Kommentare
/// überhaupt zustellt.</summary>
public interface ICommentEventProbe
{
    Task<CommentEventStatus> CheckAsync(CancellationToken ct = default);
}

public enum CommentEventState { Ok, Missing, Unknown }

/// <summary>Ergebnis samt fertiger Handlungsanweisung(en) für das Log — je Ziel eine Zeile.</summary>
public sealed record CommentEventStatus(CommentEventState State, IReadOnlyList<string> Details);
```

Registriert wird nur die Implementierung der aktiven Plattform; ohne passende
Registrierung läuft der Dienst gar nicht erst (kein `ICommentEventProbe` im Container).

### `GitHubAppCommentEventProbe`

Ein Aufruf `GET /app` mit dem vorhandenen `GitHubAppJwt` über den bereits registrierten
named Client `github-app`. Die Antwort trägt neben `slug` auch `events[]`.

- `events` enthält `pull_request_review_comment` ⇒ `Ok`
- `events` vorhanden, Eintrag fehlt ⇒ `Missing`, Detail mit dem aus `slug` gebildeten
  Deep-Link
- Nicht-200, unerwartetes JSON, Ausnahme ⇒ `Unknown`

Registriert **nur** bei `Naudit:Git:Platform=GitHub` **und** `Naudit:GitHub:Auth=App` —
dieselbe Weiche wie bei `GitHubAppEndpoints`. Im PAT-Modus gibt es keine App, deren
Ereignisliste man abfragen könnte; dieser Pfad bleibt unabgedeckt (bewusst, siehe
[Nicht enthalten](#nicht-enthalten-bewusst)).

### `GitLabCommentEventProbe`

Je Projekt `GET /api/v4/projects/{id}/hooks` mit dem über `IGitTokenProvider`
aufgelösten Token (Per-Projekt-Override wird damit respektiert). Gesucht wird der Hook,
dessen `url` auf `{Naudit:PublicBaseUrl}/webhook/gitlab` zeigt.

- Hook gefunden, `note_events == true` ⇒ Projekt gilt als sauber geprüft
- Hook gefunden, `note_events == false` ⇒ `Missing`, Detail mit Projekt-ID
- **Kein passender Hook gefunden ⇒ `Unknown`, nicht `Missing`.** `GET /projects/{id}/hooks`
  listet nur Projekt-Hooks. Wer den Wizard mit einem Gruppenziel gefahren hat, hat einen
  Gruppen-Hook, der dort nicht auftaucht — eine Warnung wäre dort dauerhaft falsch.
- Nicht-200, Ausnahme ⇒ dieses Projekt trägt nichts bei
- `Naudit:PublicBaseUrl` leer ⇒ ohne Vergleichsmaßstab sofort `Unknown` für alles

Gesamtergebnis: `Missing`, sobald **mindestens ein** Projekt eindeutig ohne
`note_events` dasteht; sonst `Ok`, wenn mindestens ein Projekt sauber geprüft wurde;
sonst `Unknown`.

**Projektauswahl:** die `ProjectEntity`-Zeilen, also die Projekte, für die Naudit schon
einmal ein Review geschrieben hat, absteigend nach `LastReviewedAt`, gedeckelt auf 20
und sequenziell abgefragt. Bekannte Folge: eine frische Installation hat keine Zeilen,
wird nicht geprüft und bleibt still. Die Warnung kommt erst nach dem ersten Review —
was genügt, weil die beiden Ereignisarten unabhängig sind: `merge_requests_events` kann
längst laufen, während `note_events` fehlt.

### `CommentEventCheckService`

`BackgroundService`; `ExecuteAsync` löst den Probe aus einem eigenen Scope auf (der
GitLab-Pfad braucht den `NauditDbContext`), führt die Prüfung einmal aus und beendet
sich. Bei `Missing` je Detail eine `LogWarning`; bei `Ok` und `Unknown` keine Ausgabe.
Der gesamte Ablauf ist in `try/catch` gekapselt — eine Diagnose darf den Host nie kippen.

## Die Log-Ausgabe

Keine Zustandsmeldung, sondern die Schritte:

```text
warn: Antwort-Kommandos sind wirkungslos — die GitHub-App ist nicht auf
      'pull_request_review_comment' abonniert. @naudit fp / @naudit ok werden nie
      zugestellt. Beheben: https://github.com/settings/apps/<slug>/permissions →
      "Subscribe to events" → "Pull request review comment" anhaken → Save. Wirkt
      sofort für bestehende Installationen; kein Permission-Wechsel, also keine
      Neuinstallation und keine Bestätigung durch die Nutzer nötig.
```

GitLab analog:

```text
warn: Antwort-Kommandos sind für Projekt 42 wirkungslos — der Webhook hat den Trigger
      "Comments" (note_events) nicht. @naudit fp / @naudit ok werden nie zugestellt.
      Beheben: Projekt → Settings → Webhooks → den Naudit-Hook bearbeiten → "Comments"
      anhaken → Save.
```

## Fehlerbehandlung

Jeder Fehlerpfad endet in `Unknown` oder einem übersprungenen Projekt, nie in einer
Ausnahme und nie in einer Warnung. Das gilt für HTTP-Fehler, fehlende Rechte,
unerwartete Antwortformate und einen fehlenden `PublicBaseUrl`. Eine Cancellation
(Shutdown) bricht sauber ab.

## Tests

Beide Probes über den vorhandenen `Fakes/StubHttpMessageHandler`:

- GitHub: `events` mit dem Eintrag ⇒ `Ok`; ohne ⇒ `Missing` und das Detail enthält den
  Slug-Link; 401/500 ⇒ `Unknown`; JSON ohne `events`-Feld ⇒ `Unknown`.
- GitLab: Hook mit `note_events: true` ⇒ `Ok`; mit `false` ⇒ `Missing` samt Projekt-ID;
  Hook-Liste ohne passende URL ⇒ `Unknown`; Nicht-200 ⇒ `Unknown`; leerer
  `PublicBaseUrl` ⇒ `Unknown` ohne HTTP-Aufruf; ein Projekt `Missing` neben einem
  sauberen ⇒ Gesamtergebnis `Missing`.
- Dienst: `Missing` ⇒ je Detail eine `LogWarning`; `Ok` und `Unknown` ⇒ keine Ausgabe;
  ein werfender Probe ⇒ keine Ausnahme nach außen.

Die Zuordnung „Projektauswahl aus der DB" wird über einen SQLite-In-Memory-Kontext
geprüft (Muster wie `DbReviewMemoryTests`): 25 Projekte ⇒ höchstens 20 Abfragen, die
nach `LastReviewedAt` jüngsten zuerst.

## Nicht enthalten (bewusst)

- **Keine Reparatur.** Weder der GitHub-Event-Liste (technisch unmöglich) noch des
  GitLab-Hooks (möglich, aber bewusst nicht getan).
- **Kein WebUI-Banner, kein Endpunkt.**
- **Der GitHub-PAT-Pfad** (Repo-Webhooks statt App) bleibt ungeprüft. Er ist der am
  wenigsten genutzte Einrichtungsweg; die Dokumentation deckt ihn seit PR #80 ab.
- **Keine wiederholte Prüfung** zur Laufzeit.
