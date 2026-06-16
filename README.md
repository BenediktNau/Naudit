# Naudit

**Naudit** ist ein selbstgehosteter Code-Review-Bot in .NET. Er reagiert auf
GitLab-Merge-Request-Webhooks, lässt das MR-Diff von einem LLM reviewen und
schreibt das Ergebnis als **einen** zusammenfassenden Markdown-Kommentar zurück
an den Merge Request.

Der AI-Provider ist über [Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/)
(`IChatClient`) **allein per Konfiguration austauschbar** — unterstützt sind
Anthropic (Claude), Ollama (lokal) und jeder OpenAI-kompatible Dienst (z. B.
NVIDIA NIM). Die Git-Plattform sitzt hinter `IGitPlatform`; GitLab ist zuerst
implementiert, GitHub kann später als zweite Implementierung folgen — ohne
Änderung am Core.

> Status: POC/MVP. Code vollständig, Test-Suite grün; der produktive End-to-End-
> Betrieb (echtes GitLab + Webhook) ist manuell zu verdrahten (siehe unten).

## Inhalt

- [Features](#features)
- [Funktionsweise](#funktionsweise)
- [Architektur](#architektur)
- [Voraussetzungen](#voraussetzungen)
- [Installation](#installation)
- [Konfiguration](#konfiguration)
- [Beispiel: lokaler Review-Durchlauf](#beispiel-lokaler-review-durchlauf)
- [GitLab-Webhook einrichten (produktiv)](#gitlab-webhook-einrichten-produktiv)
- [Tests](#tests)
- [Roadmap](#roadmap)
- [Lizenz](#lizenz)

## Features

- **Automatischer MR-Review** beim Öffnen/Aktualisieren eines Merge Requests.
- **Provider-agnostisch** über MEAI — Ollama (lokal, kein API-Key), Anthropic
  oder jeder OpenAI-kompatible Endpoint, umschaltbar per Config.
- **Asynchrone Verarbeitung** — der Webhook antwortet sofort `200`, das Review
  läuft im Hintergrund (kein GitLab-Webhook-Timeout).
- **Ein globaler System-Prompt** aus der Config, mit eingebautem Default.

## Funktionsweise

```
GitLab  ──Webhook──▶  /webhook/gitlab  ──▶  Queue  ──▶  BackgroundService
                          (sofort 200)                        │
                                                              ▼
   GitLab-Kommentar  ◀──  ReviewService  ──▶  PromptBuilder ──▶ IChatClient (LLM)
                              │  └─ holt Diff via IGitPlatform (GitLabPlatform)
                              └─ postet Summary via IGitPlatform
```

## Architektur

| Projekt | Verantwortung |
| --- | --- |
| `Naudit.Core` | Domäne, Orchestrierung (`ReviewService`, `PromptBuilder`), Abstraktionen (`IGitPlatform`). Hängt nur an den MEAI-Abstractions — kennt keinen konkreten Provider und keine Plattform. |
| `Naudit.Infrastructure` | Provider-Factory (`AiClientFactory`) + GitLab-HTTP-Client (`GitLabPlatform`) + DI-Komposition (`AddNauditInfrastructure`). |
| `Naudit.Web` | ASP.NET-Minimal-API-Host: nimmt den Webhook an, antwortet sofort `200` und verarbeitet das Review asynchron in einem `BackgroundService` (Channel-Queue). |

## Voraussetzungen

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Ein GitLab-Zugang mit Personal/Project Access Token (Scope `api`)
- Optional für lokale, kostenlose Reviews: [Ollama](https://ollama.com)
- Optional für den echten Webhook-Test: ein Tunnel wie [ngrok](https://ngrok.com)

## Installation

```bash
# 1. Repository holen
git clone <REPO-URL> naudit
cd naudit

# 2. Bauen (stellt automatisch die NuGet-Pakete wieder her)
dotnet build Naudit.slnx

# 3. Tests ausführen — sollte grün sein
dotnet test Naudit.slnx
```

> Die Solution-Datei ist `Naudit.slnx` (XML-Format), **nicht** `Naudit.sln`.

## Konfiguration

Nicht-geheime Defaults stehen in `src/Naudit.Web/appsettings.json` unter dem
Abschnitt `Naudit`. **Secrets** (`GitLab:Token`, `GitLab:WebhookSecret`,
`Ai:ApiKey`) gehören **nicht** dorthin, sondern in User-Secrets oder
Umgebungsvariablen.

```bash
dotnet user-secrets init --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://DEINE-GITLAB-INSTANZ"   --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "DEIN_TOKEN_MIT_API_SCOPE"       --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "EIN_SELBSTGEWAEHLTES_GEHEIMNIS" --project src/Naudit.Web
```

| Schlüssel | Bedeutung |
| --- | --- |
| `Naudit:GitLab:BaseUrl` | Basis-URL der GitLab-Instanz, z. B. `https://gitlab.example.com` |
| `Naudit:GitLab:Token` | Access Token mit `api`-Scope (Diff lesen, Kommentar posten) |
| `Naudit:GitLab:WebhookSecret` | Geheimnis, das gegen den Header `X-Gitlab-Token` geprüft wird |
| `Naudit:Ai:Provider` | `Ollama` \| `Anthropic` \| `OpenAICompatible` |
| `Naudit:Ai:Model` | Modellname des gewählten Providers |
| `Naudit:Ai:Endpoint` | Ollama-URL bzw. Basis-URL eines OpenAI-kompatiblen Dienstes |
| `Naudit:Ai:ApiKey` | API-Key (bei Anthropic / OpenAI-kompatibel erforderlich) |
| `Naudit:Review:SystemPrompt` | Globaler Review-Prompt; leer = eingebauter Default |

### AI-Provider wählen

Es ändert sich **nur die Konfiguration**, kein Code:

```bash
# Ollama (lokal) — Default, kein API-Key
dotnet user-secrets set "Naudit:Ai:Provider" "Ollama"                  --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "llama3.1"               --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "http://localhost:11434" --project src/Naudit.Web

# Anthropic (Claude)
dotnet user-secrets set "Naudit:Ai:Provider" "Anthropic"         --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "claude-sonnet-4-6" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "DEIN_ANTHROPIC_KEY" --project src/Naudit.Web

# OpenAI-kompatibel (z. B. NVIDIA Nemotron Ultra)
dotnet user-secrets set "Naudit:Ai:Provider" "OpenAICompatible"                       --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "https://integrate.api.nvidia.com/v1"    --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "nvidia/llama-3.1-nemotron-ultra-253b-v1" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "DEIN_NVIDIA_KEY"                         --project src/Naudit.Web
```

## Beispiel: lokaler Review-Durchlauf

Der schnellste Weg, die **komplette** Pipeline zu prüfen — mit lokalem Ollama und
einem simulierten Webhook-Event (ohne ngrok / GitLab-Webhook-Konfiguration).
Der Bot holt trotzdem das echte MR-Diff und postet einen echten Kommentar.

> **Tipp:** Nimm dafür ein **Wegwerf-Projekt mit einem Dummy-MR** in deinem
> GitLab, um keine echten MRs zuzuspammen. Du brauchst dessen **Projekt-ID**
> (Settings → General) und die **MR-IID** (die `!nummer`).

**1. Lokales LLM bereitstellen:**

```bash
ollama pull llama3.1
ollama serve            # läuft auf http://localhost:11434
```

**2. Secrets setzen** (GitLab + ein frei gewähltes Webhook-Secret):

```bash
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://DEINE-GITLAB-INSTANZ" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "TOKEN_MIT_API_SCOPE"          --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "test-secret"                  --project src/Naudit.Web
```

**3. Service starten:**

```bash
dotnet run --project src/Naudit.Web --urls http://localhost:5080
curl http://localhost:5080/health        # -> "healthy"
```

**4. Webhook-Event simulieren** (`id` = Projekt-ID, `iid` = MR-Nummer):

```bash
curl -X POST http://localhost:5080/webhook/gitlab \
  -H "X-Gitlab-Token: test-secret" \
  -H "Content-Type: application/json" \
  -d '{
        "object_kind": "merge_request",
        "project": { "id": 1234 },
        "object_attributes": { "iid": 5, "title": "Add retry to HTTP client", "action": "open" }
      }'
```

Der Endpoint antwortet **sofort `200`**; das Review läuft asynchron. Nach
wenigen Sekunden erscheint im MR `!5` ein Kommentar, etwa:

```markdown
**Zusammenfassung:** Die Retry-Logik ist sinnvoll, hat aber zwei Schwachstellen.

- `HttpClient` wird pro Aufruf neu erzeugt → Socket-Exhaustion. Stattdessen
  einen geteilten `HttpClient`/`IHttpClientFactory` verwenden.
- Die Retry-Schleife fängt jede `Exception` ab und verschluckt damit auch
  `OperationCanceledException`; das Cancellation-Token sollte durchgereicht werden.
- Kein Backoff zwischen den Versuchen — bei Lastspitzen verschärft das das Problem.
```

**Fehlersuche:** Da asynchron, steht das Ergebnis nicht in der curl-Antwort.
Bei ausbleibendem Kommentar die Service-Logs prüfen (`Review failed for MR 5`) —
typische Ursachen: falscher GitLab-Token (401), falsche Projekt-/MR-ID (404),
Ollama nicht erreichbar.

## GitLab-Webhook einrichten (produktiv)

Für den echten Betrieb liefert GitLab das Event selbst:

1. Bot öffentlich erreichbar machen — für lokale Tests per Tunnel:
   ```bash
   ngrok http 5080   # öffentliche URL notieren: https://<id>.ngrok.io
   ```
2. Im Ziel-Projekt → **Settings → Webhooks**:
   - **URL:** `https://<id>.ngrok.io/webhook/gitlab`
   - **Secret Token:** derselbe Wert wie `Naudit:GitLab:WebhookSecret`
   - **Trigger:** nur **Merge request events**
3. *Add webhook*, dann *Test → Merge request events* (oder einen echten MR öffnen).

## Tests

```bash
dotnet test Naudit.slnx                                                   # alle Tests
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests   # eine Klasse
```

Die Suite testet Core ohne Netzwerk (Fakes für LLM/GitLab), den GitLab-Client
gegen einen Stub-`HttpMessageHandler` und den Webhook-Endpoint per
`WebApplicationFactory` — ohne echtes LLM/GitLab zu treffen.

## Roadmap

Bewusst (noch) nicht im MVP enthalten:

- Inline-/Positions-Kommentare statt nur Summary
- GitHub-Adapter als zweite `IGitPlatform`-Implementierung
- Idempotenz / De-Duplizierung wiederholter Events
- Diff-Größen-Begrenzung für große MRs
- Regeln pro Repo (`.naudit.yml`)

## Lizenz

[MIT](LICENSE) © 2026 Benedikt Nau
