# Design: GitHub-Unterstützung (Config-gewählt, PAT)

*2026-06-16 · Projekt: Naudit*

## Ziel

Naudit so erweitern, dass es **GitHub Pull Requests** genauso reviewen kann wie heute
GitLab Merge Requests: PR-Diff holen, vom LLM reviewen lassen, einen Summary-Markdown-Kommentar
an den PR zurückschreiben. Der Bot soll auf **allen Repos eingesetzt werden können, bei denen
Benedikt das möchte** — operativ dadurch, dass dort der Webhook eingerichtet und der PAT Zugriff hat.

## Entscheidungen

- **Eine Plattform pro Deployment, per Config gewählt** — analog zum AI-Provider. Neuer Schlüssel
  `Naudit:Git:Platform` (`GitLab` | `GitHub`, Default `GitLab`). Nur die aktive Plattform wird
  verdrahtet. (Bewusst **nicht** beide gleichzeitig — schlank gehalten, kein Routing-Discriminator nötig.)
- **Auth über Personal Access Token (PAT)** — fine-grained PAT mit Repo-Zugriff. Webhook pro Repo
  manuell eingerichtet. (Bewusst **kein** GitHub App — JWT/Installation-Tokens wären für den POC zu viel.)
- **`Naudit.Core` bleibt unverändert.** GitHub ist die in CLAUDE.md vorgesehene zweite
  `IGitPlatform`-Implementierung in `Naudit.Infrastructure`. Die Abhängigkeitsregel
  (`Web → Infrastructure → Core`, Core nur an MEAI-Abstractions) bleibt intakt.

## Komponenten

### 1. Konfiguration & DI (`Naudit.Infrastructure`)
- Neues Enum `GitPlatformKind { GitLab, GitHub }`, Config-Key `Naudit:Git:Platform` (Default `GitLab`).
- Neue `GitHubOptions { BaseUrl = "https://api.github.com", Token, WebhookSecret }`.
- In `AddNauditInfrastructure` ein `switch` auf `GitPlatformKind`: registriert **entweder** den
  bestehenden GitLab- **oder** den neuen GitHub-typed-`HttpClient` als `IGitPlatform`. Nur die aktive
  Plattform wird registriert; die jeweils andere `*Options`-Bindung darf bestehen bleiben (ungenutzt).

### 2. `GitHubPlatform : IGitPlatform` (REST v3, PAT)
- `GetChangesAsync`: `GET repos/{owner/repo}/pulls/{number}/files`
  → mappt je Datei `filename → CodeChange.FilePath`, `patch → CodeChange.Diff`.
  Dateien ohne `patch` (binär/zu groß) werden übersprungen.
- `PostSummaryAsync`: `POST repos/{owner/repo}/issues/{number}/comments` mit Body `{ "body": <markdown> }`,
  danach `EnsureSuccessStatusCode()`.
- Pflicht-Header am typed `HttpClient` (in der DI gesetzt): `Authorization: Bearer <PAT>`,
  `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`,
  `User-Agent: Naudit` (GitHub lehnt Requests ohne User-Agent ab).

### 3. Webhook (`Naudit.Web` + `Naudit.Infrastructure/Git/GitHub`)
- Endpoint `/webhook/github` wird in `Program.cs` **nur gemappt, wenn GitHub die aktive Plattform ist**
  (`/webhook/gitlab` analog nur bei GitLab). Verhindert, dass ein Payload an die falsche Implementierung gerät.
- **Signaturprüfung:** GitHub bietet ausschließlich HMAC-SHA256. `GitHubWebhook.IsValidSignature(rawBody, secret, header)`
  berechnet HMAC-SHA256 über den **rohen Request-Body** und vergleicht **konstant-zeitlich**
  (`CryptographicOperations.FixedTimeEquals`) gegen `X-Hub-Signature-256: sha256=<hex>`.
  Leeres Secret ⇒ `401` (fail-closed, wie bei GitLab). Der rohe Body muss vor der JSON-Deserialisierung
  gelesen werden (Bytes), da die Signatur über die exakten Bytes geht.
- Event-Typ aus Header `X-GitHub-Event` == `pull_request`; reviewbare Actions:
  `opened`, `reopened`, `synchronize`.

### 4. Mapping → `ReviewRequest`
- `repository.full_name` → `ProjectId` (`"owner/repo"`)
- `pull_request.number` → `MergeRequestIid`
- `pull_request.title` → `Title`
- **`ReviewRequest` wird nicht umbenannt** (würde Core + alle Tests churnen). Die Felder sind generisch
  genug; die plattformspezifische Bedeutung ist hier dokumentiert. Da `ProjectId` bereits `owner/repo`
  enthält, bauen die `GitHubPlatform`-URLs direkt `repos/{ProjectId}/…` mit `BaseAddress = https://api.github.com/`.

## Datenfluss

```
GitHub ──Webhook(POST /webhook/github, X-Hub-Signature-256)──▶ Naudit.Web
   Web: HMAC prüfen ▶ X-GitHub-Event==pull_request & Action prüfen ▶ ToReviewRequest ▶ Queue ▶ 200
                                                  │
        ReviewBackgroundService (eigener Scope) ◀─┘
                 ▼
        ReviewService.ReviewAsync:
           1) IGitPlatform.GetChangesAsync  ──REST──▶ GitHub (pulls/{n}/files → patch)
           2) PromptBuilder.Build(systemPrompt, request, changes)
           3) IChatClient.GetResponseAsync ──────────▶ LLM
           4) IGitPlatform.PostSummaryAsync ──REST──▶ GitHub (issues/{n}/comments)
```
Der gesamte Web→Queue→Worker→ReviewService-Pfad bleibt identisch zu GitLab; nur die konkrete
`IGitPlatform` und der Webhook-Adapter sind neu.

## Tests (spiegeln das GitLab-Vorgehen)
- `GitHubPlatformTests` (mit `StubHttpMessageHandler`): `GetChangesAsync` mappt `files[].patch` und
  überspringt Dateien ohne `patch`; `PostSummaryAsync` postet an `…/issues/{n}/comments` mit Body.
- `GitHubWebhookTests`: `ToReviewRequest` mappt `pull_request`-Payload und gibt `null` zurück bei
  Nicht-`pull_request`-Events / nicht-reviewbaren Actions; `IsValidSignature` valide vs. invalide HMAC.
- `WebhookEndpointTests`-Varianten mit `WebApplicationFactory<Program>` und Config-Override
  `Naudit:Git:Platform=GitHub`: 401 bei falscher/fehlender Signatur, 200 bei Nicht-PR-Event.
  LLM/GitHub-API werden nie real getroffen.
- Bestehende GitLab-Tests bleiben unverändert grün (Default-Plattform = GitLab).

## Bewusste Grenzen / Non-Goals
- **Pagination:** PR-Files werden mit `per_page=100`, **eine Seite** geholt — reicht für normale PRs;
  sehr große PRs (>100 geänderte Dateien) werden abgeschnitten. Dokumentierte Limitierung.
- **PromptBuilder-Wording:** Der Prompt sagt weiterhin „Merge Request" — bei GitHub steht das dann im
  Kommentar. Kosmetisch, bewusst nicht generalisiert (Cosmetic-Cruft, lean).
- **Kein GitHub App**, keine Idempotenz/De-Dup, keine Retries/Timeouts, keine Diff-Größenbegrenzung —
  identisch zu den bestehenden POC-Grenzen.

## Verweise
- Repo-Architektur: `CLAUDE.md`, Vault `Naudit – Architektur`
- Bestehender Plan: `docs/superpowers/plans/2026-06-16-naudit-codereview-bot.md`
- Vault-Notiz: `2026-06-16 GitHub-Support – Design`
