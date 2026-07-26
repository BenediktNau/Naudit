# Prompt- & Kommunikations-Logging

Macht **jeden LLM-Aufruf** eines Reviews nachvollziehbar — System-Prompt, zusammengebauter
User-Prompt (Diff + Kontext + Memory + Guidelines), die rohe Antwort, Token-Usage, Latenz und
Modell. Gedacht, um beim Prompt-Tuning zu sehen, **wo** man ansetzen kann (z. B. wenn man
Autor-/Pool-Sessions einschaltet und vergleichen will, was die eigene Subscription wie beantwortet).

Aus per Default: kein Overhead, keine gespeicherten Prompts, solange niemand es einschaltet.

## Mechanik: ein Mediator-Pipeline-Behavior

Träger ist ein **Open-Source-Mediator** (`martinothamar/Mediator`, MIT, Source-Generator — bewusst
**nicht** MediatR, das seit 2025-07 kommerziell ist). Er lebt **vollständig in
`Naudit.Infrastructure`**; die zentrale Core-Regel (Core kennt nur MEAI-Abstraktionen) bleibt
intakt — `ReviewService` ist unverändert.

```
ReviewService (Core)
  └─ IChatClient.GetResponseAsync            ← Core sieht nur diese MEAI-Naht
       └─ MediatorChatClient (Decorator)     ← Infrastructure, nur bei Logging=on
            └─ mediator.Send(ChatCompletionRequest)
                 └─ PromptLoggingBehavior     ← die "Middleware": Log + Persistenz
                      └─ ChatCompletionHandler → echter IChatClient (global/Session)
```

- `MediatorChatClient` (`Ai/Logging/`) umhüllt den globalen Client (Single-Modus + Session-Fallback)
  und den Autor-/Pool-Session-Client (`SessionSelectionFactory`), sodass auch der „fast"-Pfad
  (eigene Subscription) protokolliert wird.
- `PromptLoggingBehavior` loggt strukturiert (ILogger) und persistiert best-effort ein
  `ChatTranscriptEntity`. **Fail-open:** ein Fehler beim Loggen/Persistieren kippt nie das Review;
  nur eine echte Aufruf-Exception wird — nach Erfassung eines `Failed`-Transcripts — weitergereicht.
- Zuordnung Review ⇄ Transcript über eine `CorrelationId` (AsyncLocal `IReviewCorrelationAccessor`,
  gesetzt am Review-Eintritt in `ReviewBackgroundService` / `POST /review`). Kein FK — die
  Transcripts entstehen, bevor die `ReviewEntity`-Audit-Zeile geschrieben wird; der Audit-Sink
  spiegelt dieselbe `CorrelationId` auf das Review.

## Konfiguration (`Naudit:Ai:Logging`, DB-verwaltbar)

| Key | Default | Wirkung |
| --- | --- | --- |
| `Enabled` | `false` | Master-Schalter. Aus ⇒ der Client wird gar nicht erst umhüllt. |
| `IncludePrompts` | `true` | System-/User-Prompt-Volltext in Log/DB (sonst nur Metadaten). |
| `IncludeResponse` | `true` | Rohe LLM-Antwort in Log/DB. |
| `Persist` | `true` | Transcript in die DB (WebUI). Aus ⇒ nur strukturiertes ILogger-Logging. |
| `MaxCharsPerField` | `0` | Kappung gespeicherter Prompt-/Antwort-Länge (0 = unbegrenzt). |

Ändern über die WebUI-Settings (Katalog-Keys) — ein Restart übernimmt sie (das Umhüllen wird zur
DI-Registrierzeit entschieden).

## WebUI

Im Review-Detail (Dashboard → Review aufklappen) erscheint bei aktivem Logging ein aufklappbares
Panel **„Prompt & Kommunikation"** pro Transcript: System-Prompt, User-Prompt, rohe Antwort,
Modell/Latenz/Token/Tool-Anzahl. **Nur für Admins** — die Volltexte enthalten (redigierten)
Quellcode (`GET /api/reviews/{id}` liefert `transcripts` nur an Admins).

## Sicherheit / Datenschutz

Prompts laufen durch die [Redaction](redaction.md) (Secrets/IPs/E-Mails maskiert), **bevor** sie
gebaut und damit auch, bevor sie protokolliert werden. Dennoch enthalten sie Quellcode-Diffs:
Persistenz ist opt-in, die WebUI-Volltextansicht admin-only, und `MaxCharsPerField` begrenzt die
DB-Größe.

## Umfang / bewusste Grenzen

- **DAST**-Probing nutzt einen eigenen, un-umhüllten Basis-Client (`DependencyInjection.cs`,
  `dastBaseClient`) und wird (noch) nicht protokolliert. Die Guideline-Destillation dagegen läuft
  über den globalen (umhüllten) Client innerhalb des Review-Flows und erscheint daher — wenn sie
  überhaupt anläuft (hash-gecacht) — als zusätzliche Transcript-Zeile desselben Reviews.
- Auf dem **Fallback-Pfad** (Autor-Session scheitert → globaler Client) entstehen zwei Zeilen: ein
  `Failed`-Versuch (Session) und der erfolgreiche globale Aufruf — bewusst, als ehrliches Protokoll.

## Erweiterungspunkt (Core-Regel wahren)

Weitere Pipeline-Schritte (z. B. ein Metriken- oder Kosten-Behavior) sind einfach ein weiteres
`IPipelineBehavior<ChatCompletionRequest, ChatResponse>` in `MediatorOptions.PipelineBehaviors`
(`DependencyInjection.cs`). Eine andere Transcript-Senke ist eine weitere `IChatTranscriptSink`-Impl
— beides reine Infrastructure-Nähte, kein Core-Eingriff.
