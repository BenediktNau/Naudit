# Configuration

Non-secret defaults live in `src/Naudit.Web/appsettings.json` under the `Naudit`
section. **Secrets** (`GitLab:Token`, `GitLab:WebhookSecret`, `GitHub:Token`,
`GitHub:WebhookSecret`, `Ai:ApiKey`) do **not** belong there — put them in
user-secrets, environment variables, or your deployment environment's secret
management.

> In a container the keys are set as environment variables — ASP.NET maps `:` to
> `__`, e.g. `Naudit:Ai:ApiKey` → `Naudit__Ai__ApiKey`.

For local development via user-secrets:

```bash
dotnet user-secrets init --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://YOUR-GITLAB-INSTANCE"   --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "YOUR_TOKEN_WITH_API_SCOPE"      --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "A_SELF_CHOSEN_SECRET"           --project src/Naudit.Web
```

## Keys

| Key | Meaning |
| --- | --- |
| `Naudit:Git:Platform` | `GitLab` (default) \| `GitHub` — selects the active platform |
| `Naudit:GitLab:BaseUrl` | Base URL of the GitLab instance, e.g. `https://gitlab.example.com` |
| `Naudit:GitLab:Token` | Access token with `api` scope (read diff, post comment) |
| `Naudit:GitLab:WebhookSecret` | Secret checked against the `X-Gitlab-Token` header |
| `Naudit:GitHub:BaseUrl` | Base URL of the GitHub API (default: `https://api.github.com`) |
| `Naudit:GitHub:Token` | Fine-grained PAT (see [Platform setup](platform-setup.md)) |
| `Naudit:GitHub:WebhookSecret` | Secret for HMAC-SHA256 verification (`X-Hub-Signature-256`) |
| `Naudit:Ai:Provider` | `Ollama` \| `Anthropic` \| `OpenAICompatible` \| `ClaudeCode` |
| `Naudit:Ai:Model` | Model name for the chosen provider |
| `Naudit:Ai:Endpoint` | Ollama URL or base URL of an OpenAI-compatible service |
| `Naudit:Ai:ApiKey` | API key (required for Anthropic / OpenAI-compatible) |
| `Naudit:Review:SystemPrompt` | Global review prompt; empty = built-in default |
| `Naudit:Review:Gate:MinSeverity` | Lowest finding severity that can block the merge — `Info` \| `Low` \| `Medium` \| `High` \| `Critical` (default `High`) (see [Review gate](review-gate.md)) |
| `Naudit:Review:Gate:MinConfidence` | Lowest LLM confidence that can block the merge — `Low` \| `Medium` \| `High` (default `Medium`) |
| `Naudit:Redaction:Enabled` | Mask secrets/IPs/e-mails before the prompt — **default `true`** (see [Prompt redaction](redaction.md)) |
| `Naudit:Redaction:EntropyThreshold` | Shannon bits/char for the high-entropy secret fallback (default `4.0`) |
| `Naudit:Redaction:MinEntropyTokenLength` | Minimum token length checked by the entropy pass (default `20`) |

## Choosing an AI provider

Only the **configuration** changes, no code:

```bash
# Ollama (local) — default, no API key
dotnet user-secrets set "Naudit:Ai:Provider" "Ollama"                  --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "llama3.1"               --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "http://localhost:11434" --project src/Naudit.Web

# Anthropic (Claude)
dotnet user-secrets set "Naudit:Ai:Provider" "Anthropic"          --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "claude-sonnet-4-6"  --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "YOUR_ANTHROPIC_KEY" --project src/Naudit.Web

# OpenAI-compatible (e.g. NVIDIA Nemotron Ultra)
dotnet user-secrets set "Naudit:Ai:Provider" "OpenAICompatible"                       --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Endpoint" "https://integrate.api.nvidia.com/v1"    --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "nvidia/llama-3.1-nemotron-ultra-253b-v1" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:ApiKey"   "YOUR_NVIDIA_KEY"                         --project src/Naudit.Web

# ClaudeCode (local `claude` CLI, subscription instead of API key — see docs/claudecode-provider.md)
dotnet user-secrets set "Naudit:Ai:Provider" "ClaudeCode" --project src/Naudit.Web
dotnet user-secrets set "Naudit:Ai:Model"    "sonnet"     --project src/Naudit.Web
# Auth: set CLAUDE_CODE_OAUTH_TOKEN in the environment (from `claude setup-token`); no Naudit:Ai:ApiKey needed.
```
