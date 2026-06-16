# Naudit

Naudit ist ein **selbstgehosteter Code-Review-Bot** in .NET. Er reagiert auf
GitLab-Merge-Request-Webhooks, lässt das MR-Diff von einem LLM reviewen und
schreibt das Ergebnis als **einen** zusammenfassenden Markdown-Kommentar zurück
an den Merge Request.

Der AI-Provider ist über
[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/) (`IChatClient`)
**allein per Konfiguration austauschbar** — unterstützt sind Anthropic (Claude),
Ollama (lokal) und jeder OpenAI-kompatible Dienst (z. B. NVIDIA NIM). Die
Git-Plattform sitzt hinter `IGitPlatform`; GitLab ist zuerst implementiert,
GitHub kann später als zweite Implementierung folgen — ohne Änderung am Core.

## Architektur

| Projekt | Verantwortung |
| --- | --- |
| `Naudit.Core` | Domäne, Orchestrierung (`ReviewService`), Abstraktionen (`IGitPlatform`). Hängt nur an den MEAI-Abstractions — kennt keinen konkreten Provider und keine Plattform. |
| `Naudit.Infrastructure` | Provider-Factory (`AiClientFactory`) + GitLab-HTTP-Client (`GitLabPlatform`). |
| `Naudit.Web` | ASP.NET-Minimal-API-Host: nimmt den Webhook an, antwortet sofort `200` und verarbeitet das Review asynchron in einem `BackgroundService` (Channel-Queue). |

## Voraussetzungen

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- Optional für lokale Reviews: [Ollama](https://ollama.com)
- Ein GitLab-Zugang mit Personal/Project Access Token (Scope `api`)

## Build & Test

```bash
dotnet build Naudit.slnx
dotnet test  Naudit.slnx
```

## Lokal starten

```bash
dotnet run --project src/Naudit.Web
# -> Now listening on: http://localhost:5xxx
curl http://localhost:5xxx/health   # -> "healthy"
```

## Konfiguration

Nicht-geheime Defaults stehen in `src/Naudit.Web/appsettings.json` unter dem
Abschnitt `Naudit`. **Secrets** (`GitLab:Token`, `GitLab:WebhookSecret`,
`Ai:ApiKey`) gehören **nicht** dorthin, sondern in User-Secrets oder
Umgebungsvariablen.

```bash
dotnet user-secrets init --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://DEINE-GITLAB-INSTANZ" --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "DEIN_TOKEN_MIT_API_SCOPE"     --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "EIN_SELBSTGEWAEHLTES_GEHEIMNIS" --project src/Naudit.Web
```

### AI-Provider wählen

Es ändert sich **nur die Konfiguration**, kein Code:

```bash
# Ollama (lokal) — Default
dotnet user-secrets set "Naudit:Ai:Provider" "Ollama"                  --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "llama3.1"               --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "http://localhost:11434" --project src/Naudit.Web

# Anthropic (Claude)
dotnet user-secrets set "Naudit:Ai:Provider" "Anthropic"        --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "claude-sonnet-4-6" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "DEIN_ANTHROPIC_KEY" --project src/Naudit.Web

# OpenAI-kompatibel (z. B. NVIDIA Nemotron Ultra)
dotnet user-secrets set "Naudit:Ai:Provider" "OpenAICompatible"                       --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "https://integrate.api.nvidia.com/v1"    --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "nvidia/llama-3.1-nemotron-ultra-253b-v1" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "DEIN_NVIDIA_KEY"                         --project src/Naudit.Web
```

Der Review-System-Prompt ist global und kommt aus `Naudit:Review:SystemPrompt`;
bleibt der Wert leer, greift der eingebaute Default-Prompt.

## GitLab-Webhook einrichten

1. Bot erreichbar machen — für lokale Tests z. B. per Tunnel:
   ```bash
   ngrok http 5xxx   # öffentliche URL notieren: https://<id>.ngrok.io
   ```
2. Im Ziel-Projekt → **Settings → Webhooks**:
   - **URL:** `https://<id>.ngrok.io/webhook/gitlab`
   - **Secret Token:** derselbe Wert wie `Naudit:GitLab:WebhookSecret`
   - **Trigger:** nur **Merge request events**
3. *Add webhook*, dann *Test → Merge request events* (oder einen echten MR öffnen).

Erwartung: Im MR erscheint ein Naudit-Kommentar mit der Review-Zusammenfassung.
Bei Fehlern die Service-Logs prüfen (`Review failed for MR ...`).

## Bewusst (noch) nicht im MVP

Inline-/Positions-Kommentare, Idempotenz bei wiederholten Events,
Diff-Größen-Begrenzung und Regeln pro Repo (`.naudit.yml`) — alles spätere
Erweiterungen.
