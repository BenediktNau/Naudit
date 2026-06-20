# Design: CI-Gate über synchronen `POST /review`-Endpoint

*2026-06-18 · Projekt: Naudit*

## Ziel

Naudit zusätzlich zum Webhook über die **CI/CD-Pipeline** ansteuerbar machen: Ein Pipeline-Job
ruft einen authentifizierten HTTP-Endpoint auf, Naudit reviewt den genannten MR/PR **synchron**
(inline), postet den Summary-Kommentar wie gehabt und gibt ein **strukturiertes Verdict** zurück.
Anhand des Verdicts kann der Pipeline-Job grün/rot werden und so den Merge **gaten**.

Der Webhook-Pfad bleibt davon unberührt. Der „andere Integrationsweg" ist technisch nur eine
zweite Quelle, die ein `ReviewRequest` erzeugt — der Review-Kern (`ReviewService`) ist bereits
auslöser-unabhängig.

## Entscheidungen

- **Ausführungsmodell: Pipeline ruft den laufenden Service** (kein eigenständiges CLI-Tool).
  Naudit läuft weiter als Web-Service; die Pipeline triggert per HTTP statt per Webhook.
- **Synchron, inline (Queue umgangen).** Der neue Endpoint führt `ReviewService.ReviewAsync`
  direkt im Request-Scope aus und antwortet erst, wenn der Kommentar gepostet wurde. Die bestehende
  `ReviewQueue`/`ReviewBackgroundService`-Mechanik bleibt ausschließlich für den Webhook-Pfad.
- **Inhaltliches Gate über ein strukturiertes Verdict.** Das LLM liefert zusätzlich zum
  Markdown-Text ein typisiertes Urteil (`approve` | `request_changes`). Realisiert über **MEAI
  Structured Output** (`GetResponseAsync<ReviewResult>`) — ein einziger Call, kein Provider-SDK in
  Core. Fallback bei Provider-Inkompatibilität: Verdict-Konvention im Text (siehe Non-Goals/Risiko).
- **Auth: Webhook-Secret wiederverwenden, aber als einfacher Header-Token** (kein HMAC).
  Der Endpoint vergleicht `X-Naudit-Token` **konstant-zeitlich** gegen das `WebhookSecret` der
  aktiven Plattform. Kein neuer Config-Key. Fail-closed → `401`.
- **Response: immer `200` bei erfolgreichem Review, Verdict im Body.** `{ "verdict": "approve" |
  "request_changes" }`. Naudit-interne Fehler → `5xx`, Auth-Fehler → `401`. Das CI-Script wertet
  `.verdict` aus und exit-codet bei `request_changes`. So bleibt „Naudit kaputt" (non-2xx, Infra)
  sauber getrennt von „Review will Änderungen" (2xx + Verdict, Gate-Entscheidung).
- **`Naudit.Core` bleibt SDK-frei.** Structured Output läuft über die MEAI-Abstraktion
  (`IChatClient.GetResponseAsync<T>`). Die Abhängigkeitsregel (`Web → Infrastructure → Core`,
  Core nur an MEAI-Abstractions) bleibt intakt.

## Komponenten

### 1. Strukturiertes Ergebnis (`Naudit.Core`)
- Neues Modell `ReviewResult(string Markdown, ReviewVerdict Verdict)` und
  `enum ReviewVerdict { Approve, RequestChanges }`. **Bewusst keine** Severity-Stufen (YAGNI;
  zwei Zustände reichen fürs Gate).
- `ReviewService.ReviewAsync` gibt künftig `ReviewResult` zurück (statt `Task` ohne Wert):
  - Holt Changes → bei **keinen Changes** sofort `ReviewVerdict.Approve` (nichts zu blocken),
    es wird wie bisher nichts gepostet.
  - Sonst: `GetResponseAsync<ReviewResult>`-artiger Aufruf liefert `{ Markdown, Verdict }`,
    postet `Markdown` via `IGitPlatform.PostSummaryAsync` und gibt das `ReviewResult` zurück.
- `PromptBuilder`: System-Prompt um die Verdict-Anweisung / das Antwortschema erweitern
  (Modell soll Summary **und** Verdict liefern).
- **Strukturierte Deserialisierung** erfolgt über MEAI (`ChatResponse<T>.Result`). Das konkrete
  Antwort-Schema (`ReviewResult`) lebt in Core; nur MEAI-Abstractions als Abhängigkeit.

### 2. Neuer Endpoint `POST /review` (`Naudit.Web`)
- **Immer gemappt**, plattformunabhängig, parallel zu den Webhook-Endpoints.
- Request-Body (JSON) → direkt auf `ReviewRequest`:
  `{ "projectId": "<id|owner/repo>", "mergeRequestIid": <int>, "title": "<text>" }`.
  Bei GitHub ist `projectId` = `owner/repo` und `mergeRequestIid` = PR-Nummer (passt zur
  bestehenden plattform-neutralen `ReviewRequest`-Semantik; keine Plattform-Verzweigung nötig).
- **Auth:** Header `X-Naudit-Token`, konstant-zeitlich (`CryptographicOperations.FixedTimeEquals`)
  gegen das `WebhookSecret` der aktiven Plattform verglichen. Leeres Secret ⇒ `401` (fail-closed).
- Führt `ReviewService.ReviewAsync` **inline im Request-Scope** aus (Minimal-API-Handler erhalten
  scoped Services), wartet auf den `ReviewResult` und gibt den Verdict zurück.
- **Response:** `200` mit `{ "verdict": "approve" | "request_changes" }` bei erfolgreichem Review
  (inkl. „keine Changes" → `approve`). Auth → `401`. Naudit-/LLM-/Git-Fehler propagieren als `5xx`.

### 3. Pipeline-Snippet (Docs)
- `.gitlab-ci.yml`-Job, der **nur in MR-Pipelines** läuft, `POST $NAUDIT_URL/review` mit
  Header `X-Naudit-Token: $NAUDIT_TOKEN` und Body aus den Predefined Variables
  (`CI_PROJECT_ID`, `CI_MERGE_REQUEST_IID`, `CI_MERGE_REQUEST_TITLE`) curlt, das `verdict`-Feld
  parst und bei `request_changes` mit Exit-Code ≠ 0 abbricht.
- GitHub-Actions-Äquivalent (Repo = `${{ github.repository }}`, PR = `${{ github.event.pull_request.number }}`),
  falls GitHub die aktive Plattform ist.

## Datenfluss

```
CI-Job ──POST /review (X-Naudit-Token, {projectId, mergeRequestIid, title})──▶ Naudit.Web
   Web: Token konstant-zeitlich prüfen ▶ Body → ReviewRequest
                 ▼  (inline, Request-Scope — KEINE Queue)
        ReviewService.ReviewAsync:
           1) IGitPlatform.GetChangesAsync  ──REST──▶ GitLab/GitHub
              (keine Changes ⇒ Verdict=Approve, nichts posten, fertig)
           2) PromptBuilder.Build(systemPrompt, request, changes)
           3) IChatClient.GetResponseAsync<ReviewResult> ─▶ LLM  (Summary + Verdict)
           4) IGitPlatform.PostSummaryAsync ──REST──▶ GitLab/GitHub  (Markdown-Kommentar)
                 ▼
   Web: 200 { "verdict": "approve" | "request_changes" }
                 ▼
   CI-Script: verdict == request_changes ⇒ exit 1 (Merge geblockt)
```
Der Webhook-Pfad (`/webhook/gitlab|github → Queue → ReviewBackgroundService → ReviewService`)
bleibt 1:1 bestehen.

## Tests (TDD, spiegeln das bestehende Vorgehen)
- **Core – `ReviewServiceTests`** (mit `FakeChatClient`/`FakeGitPlatform`):
  - liefert `Verdict=Approve` und postet, wenn das Modell `approve` liefert;
  - liefert `Verdict=RequestChanges` und postet, wenn das Modell `request_changes` liefert;
  - liefert `Verdict=Approve` und postet **nichts**, wenn es keine Changes gibt.
  - `FakeChatClient` muss eine strukturierte Antwort (`ReviewResult`) liefern können.
- **Web – `ReviewEndpointTests`** mit `WebApplicationFactory<Program>` + Fakes:
  - `401` bei fehlendem/falschem `X-Naudit-Token`;
  - `200` + korrekter Verdict-Body bei gültigem Token;
  - Body → `ReviewRequest`-Mapping (projectId/mergeRequestIid/title);
  - LLM und Git-API werden nie real getroffen.

## Bewusste Grenzen / Non-Goals
- **Provider-Risiko Structured Output:** `GetResponseAsync<ReviewResult>` hängt davon ab, dass der
  aktive Provider (Anthropic via Tool/JSON-Mode bzw. OpenAI via JSON-Schema) sauberes strukturiertes
  Output liefert. **Mitigation:** kleiner Spike in der ersten Implementierungsaufgabe; falls
  unzuverlässig, Fallback auf eine **Verdict-Konvention im Text** (Modell schreibt eine maschinen-
  lesbare `VERDICT:`-Zeile, Server parst + entfernt sie). Das Core-Interface (`ReviewResult`) bleibt
  in beiden Fällen identisch.
- **Keine Queue für den CI-Pfad** — bewusst synchron; lange Reviews halten den Request offen
  (akzeptabel für CI; Pipeline-Job hat ohnehin ein Timeout).
- **Keine Severity-Stufen, kein separates Auth-Token-Feld, keine Idempotenz/Retries** — gleiche
  POC-Grenzen wie der Webhook-Pfad.
- **`ReviewRequest` wird nicht umbenannt** (Core/Tests-Churn vermeiden); Felder sind plattform-neutral.

## Verweise
- Repo-Architektur: `CLAUDE.md`
- Vorheriger Spec: `docs/superpowers/specs/2026-06-16-github-support-design.md`
- Bestehender Plan: `docs/superpowers/plans/2026-06-16-naudit-codereview-bot.md`
