# Configuration

## Where configuration lives

Configuration comes from three layers, in increasing precedence:

```
appsettings.json  <  database (Settings page)  <  user-secrets / environment variables
```

Most `Naudit:*` keys are **DB-managed**: they ship with a built-in default and can be
changed by an admin on the WebUI **Settings** page — no redeploy needed, just a restart
(triggered from the same page) to apply. A key set via user-secrets or an environment
variable always **wins over the database** and shows up on the Settings page as
**locked** ("via environment") — an env-complete deployment is therefore unaffected by
the Settings page; the page is for the keys you *haven't* pinned via env. The exact list
of DB-managed keys is the whitelist in
[`SettingsCatalog.cs`](../src/Naudit.Infrastructure/Settings/SettingsCatalog.cs) — it
covers platform/AI/review/access-gate/sign-in settings, but not list-shaped config
(`ProjectTokens`, `Ui:Admins`), the admin seed (`Ui:Admin:Username`/`InitialPassword` —
**required on first start** to create the initial local admin, unless you sign in through
an external provider whose username is in `Ui:Admins`), or the redaction/review-context
tuning knobs, which stay user-secrets/environment-only for now.

A small set of **bootstrap keys** must stay environment-only, full stop — they are
needed *before* the database can be opened, or they configure the transport itself, so
putting them in the database would be circular: `Naudit:Db:Provider`,
`Naudit:Db:ConnectionString`, `Naudit:ForwardedHeaders:*`, and the listening
port/URLs (`ASPNETCORE_URLS`). These never appear on the Settings page.

**Secrets** (🔒 in the table below) stored via the Settings page are **encrypted at
rest** in the database (ASP.NET Data Protection) and **write-only** through the API — it
reports whether a secret is set, never its value. Honest caveat: the Data Protection key
ring lives in the same database as the encrypted values, so this guards against
accidental exposure (a leaked table dump, a stray log line) — not against an attacker
who already has full read access to the database.

Non-secret defaults also still live in `src/Naudit.Web/appsettings.json` under the
`Naudit` section. **Secrets** (`GitLab:Token`, `GitLab:WebhookSecret`, `GitHub:Token`,
`GitHub:WebhookSecret`, any `ProjectTokens:*:Token`, `Ai:ApiKey`) do **not** belong
there — put them in user-secrets, environment variables, the Settings page, or your
deployment environment's secret management.

> In a container the keys are set as environment variables — ASP.NET maps `:` to
> `__`, e.g. `Naudit:Ai:ApiKey` → `Naudit__Ai__ApiKey`.

For local development via user-secrets:

```bash
dotnet user-secrets init --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:BaseUrl"       "https://YOUR-GITLAB-INSTANCE"   --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:Token"         "YOUR_TOKEN_WITH_API_SCOPE"      --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:WebhookSecret" "A_SELF_CHOSEN_SECRET"           --project src/Naudit.Web
```

## Setup mode

On first start with an empty database, Naudit checks the **effective** config (database +
env) for the required key set below, per chosen platform/provider. If anything is missing,
the host comes up in **setup mode**: the webhook endpoints, `POST /review`, and the review
pipeline stay unmapped, and the WebUI serves the [setup wizard](webui.md#setup-wizard)
instead of the normal app. An env-complete deployment never sees it. See
[WebUI › Setup wizard](webui.md#setup-wizard) for the guided flow; this table is the
reference for driving it via env vars/user-secrets instead.

| Scenario | Required keys |
| --- | --- |
| GitLab | `Naudit:GitLab:BaseUrl`, `Naudit:GitLab:Token`, `Naudit:GitLab:WebhookSecret` |
| GitHub (PAT) | `Naudit:GitHub:Token`, `Naudit:GitHub:WebhookSecret` |
| GitHub (App) | `Naudit:GitHub:App:AppId`, `Naudit:GitHub:App:PrivateKey`, `Naudit:GitHub:WebhookSecret` |
| AI | `Naudit:Ai:Model` (not required for `ClaudeCode`, which defaults to `sonnet`); additionally `Naudit:Ai:ApiKey` for `Anthropic`/`OpenAICompatible` |

`Naudit:Ai:Endpoint` is **never** in the required set — every provider that reads it has a
working default (`http://localhost:11434` for Ollama; OpenAI-compatible endpoints need it
in practice but setup mode doesn't hard-require it). Guiding principle: a **missing** value
drops you into setup mode (the wizard asks for it); an **invalid** value (an enum that
doesn't parse, e.g. a typo'd `Naudit:Git:Platform`) instead trips **recovery mode** — see
[WebUI › Settings are editable](webui.md#settings-are-editable).

## Keys

| Key | Meaning |
| --- | --- |
| `Naudit:PublicBaseUrl` | The externally reachable base URL of this instance, e.g. `https://naudit.example.com`. Set by the [setup wizard](#setup-mode) (step 2) or manually; backs the webhook URLs shown on the wizard's completion page and the GitHub App manifest redirect URL |
| `Naudit:Git:Platform` | `GitLab` (default) \| `GitHub` — selects the active platform |
| `Naudit:GitLab:BaseUrl` | Base URL of the GitLab instance, e.g. `https://gitlab.example.com` |
| `Naudit:GitLab:Token` | Access token with `api` scope (read diff, post comment) — global fallback |
| `Naudit:GitLab:WebhookSecret` | Secret checked against the `X-Gitlab-Token` header |
| `Naudit:GitLab:ProjectTokens:<n>:Project` / `:Token` | Optional per-project token override (`Project` = numeric project ID); falls back to `Naudit:GitLab:Token` (see [Per-project tokens](#per-project-tokens)) |
| `Naudit:GitHub:BaseUrl` | Base URL of the GitHub API (default: `https://api.github.com`). For **GitHub Enterprise (GHES)** the [setup wizard](webui.md#setup-wizard) derives the API base `{host}/api/v3` from the GHES web host — but **only in the `Auth=App` flow** (you enter the GHES host, not the API URL). In PAT mode (`Auth=Pat`) a GHES API URL must be set manually |
| `Naudit:GitHub:Token` | Fine-grained PAT (see [Platform setup](platform-setup.md)) — global fallback |
| `Naudit:GitHub:WebhookSecret` | Secret for HMAC-SHA256 verification (`X-Hub-Signature-256`) |
| `Naudit:GitHub:ProjectTokens:<n>:Project` / `:Token` | Optional per-project fine-grained PAT (`Project` = `owner/repo`); falls back to `Naudit:GitHub:Token` (see [Per-project tokens](#per-project-tokens)) |
| `Naudit:GitHub:Auth` | `Pat` (default) or `App` — how Naudit authenticates against GitHub |
| `Naudit:GitHub:App:AppId` | GitHub App ID (required for `Auth=App`) |
| `Naudit:GitHub:App:PrivateKey` | App private key: raw PEM or base64-encoded PEM (required for `Auth=App`; secret!) |
| `Naudit:GitHub:App:InstallationId` | Optional: fixed installation id (skips the per-repo lookup) |
| `Naudit:GitHub:PostVerdict` | `true` posts a real review state (`APPROVE`/`REQUEST_CHANGES`); default `false` = `COMMENT` |
| `Naudit:GitLab:PostVerdict` | `true` calls MR `approve`/`unapprove` from the verdict; default `false` |
| `Naudit:Ai:Provider` | `Ollama` \| `Anthropic` \| `OpenAICompatible` \| `ClaudeCode` |
| `Naudit:Ai:Model` | Model name for the chosen provider |
| `Naudit:Ai:Endpoint` | Ollama URL or base URL of an OpenAI-compatible service |
| `Naudit:Ai:ApiKey` | API key (required for Anthropic / OpenAI-compatible) |
| `Naudit:Review:SystemPrompt` | Global review prompt; empty = built-in default |
| `Naudit:Review:Gate:MinSeverity` | Lowest finding severity that can block the merge — `Info` \| `Low` \| `Medium` \| `High` \| `Critical` (default `High`) (see [Review gate](review-gate.md)) |
| `Naudit:Review:Gate:MinConfidence` | Lowest LLM confidence that can block the merge — `Low` \| `Medium` \| `High` (default `Medium`) |
| `Naudit:Review:MaxRoundtrips` | Max automatic (webhook-triggered) reviews per MR/PR; further pushes are skipped and the last allowed review notes it in its summary — `0` = unlimited (default `3`). The synchronous CI trigger `POST /review` is never limited. |
| `Naudit:Review:Context:Enabled` | Enrich the prompt with surrounding code / usages / repo overview from the checkout — **default `true`** (see [Review context](review-context.md)) |
| `Naudit:Review:Context:MaxChars` | Character budget for the context section (default `40000`) |
| `Naudit:Review:Context:FullFileMaxLines` | Changed file ≤ this ⇒ whole file in context; larger ⇒ block excerpts (default `400`) |
| `Naudit:Review:Context:BlockPadLines` | ± fallback window around a hunk anchor when the block heuristic is too tight (default `30`) |
| `Naudit:Review:Context:UsageSnippetLines` | ± lines around each call-site (default `3`) |
| `Naudit:Review:Context:MaxUsagesPerSymbol` | Max call-sites shown per changed symbol (default `5`) |
| `Naudit:Review:Context:MaxTreeDepth` | Directory-tree depth in the overview (default `3`) |
| `Naudit:Review:Context:ReadmeMaxLines` | README head length in the overview (default `50`) |
| `Naudit:Redaction:Enabled` | Mask secrets/IPs/e-mails before the prompt — **default `true`** (see [Prompt redaction](redaction.md)) |
| `Naudit:Redaction:EntropyThreshold` | Shannon bits/char for the high-entropy secret fallback (default `4.0`) |
| `Naudit:Redaction:MinEntropyTokenLength` | Minimum token length checked by the entropy pass (default `20`) |
| `Naudit:AccessGate:Mode` | `Open` (default) — every project with a valid webhook secret is reviewed \| `Registered` — only projects of active WebUI accounts (see [WebUI › Access model](webui.md#access-model)) |
| `Naudit:Db:Provider` | `Sqlite` (default) \| `Postgres` — persistence backend (same schema/migrations for both) — **bootstrap key, env-only** |
| `Naudit:Db:ConnectionString` | Connection string for the chosen backend — **bootstrap key, env-only**. App default `Data Source=data/naudit.db` (relative path, for the self-contained-binary case); the container image overrides this to `Data Source=/data/naudit.db` (mount a volume!) or Postgres `Host=…;Database=…;Username=…;Password=…` |
| `Naudit:Ui:Admin:Username` / `:InitialPassword` | Seed admin, created once on first start with an empty database (secret!) — env-only, not yet DB-managed |
| `Naudit:Ui:Admins` | Usernames that get the admin role on (external) sign-in — env-only, not yet DB-managed (list-shaped) |
| `Naudit:Ui:Auth:GitHub:Enabled` / `:ClientId` / `:ClientSecret` | GitHub-OAuth self-service sign-in (opt-in; secret!) |
| `Naudit:Ui:Auth:Oidc:Enabled` / `:Authority` / `:ClientId` / `:ClientSecret` | OIDC/Keycloak self-service sign-in (opt-in; secret!) |

> With `Naudit:GitHub:Auth = App`, `Naudit:GitHub:Token` and `Naudit:GitHub:ProjectTokens` are
> **ignored** — every request uses a freshly minted GitHub App installation token instead. See
> [GitHub App setup](github-app.md) for creating the app, its permissions, and the install step.

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

## Per-project tokens

By default every repository is accessed with the single global token
(`Naudit:GitHub:Token` / `Naudit:GitLab:Token`). You can override the token **per
project** — e.g. to give each repository its own fine-grained token with access to
that repo only. At review time Naudit resolves the token from the project of the
incoming webhook/PR/MR:

- **override wins** if the project is listed, **otherwise** the global token is used;
- an empty override entry is ignored (never an empty auth header);
- the `Project` key is matched case-insensitively.

The `Project` value must match the project identifier exactly as it arrives from the
platform: for **GitHub** that is `owner/repo`, for **GitLab** the **numeric project
ID** (Project overview / *Settings → General*), not the path.

`ProjectTokens` is a **list** (not a map) on purpose: the `owner/repo` value stays in
the *value*, so the config is also settable via environment variables (Coolify/Docker),
whose names cannot contain a slash.

**Local development (user-secrets):**

```bash
# GitHub — per-repo fine-grained PAT
dotnet user-secrets set "Naudit:GitHub:ProjectTokens:0:Project" "octo/hello-world"  --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitHub:ProjectTokens:0:Token"   "github_pat_..."     --project src/Naudit.Web

# GitLab — per-project token (numeric project ID)
dotnet user-secrets set "Naudit:GitLab:ProjectTokens:0:Project" "12345"              --project src/Naudit.Web
dotnet user-secrets set "Naudit:GitLab:ProjectTokens:0:Token"   "glpat-..."          --project src/Naudit.Web
```

**Container / Coolify (environment variables — `:` maps to `__`):**

```
Naudit__GitHub__ProjectTokens__0__Project = octo/hello-world
Naudit__GitHub__ProjectTokens__0__Token   = github_pat_...
Naudit__GitHub__ProjectTokens__1__Project = octo/other
Naudit__GitHub__ProjectTokens__1__Token   = github_pat_...
```

> The token map is read **once at startup**. Adding or changing a per-project token
> takes effect after a **restart/redeploy** of the service.
