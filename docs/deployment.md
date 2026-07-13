# Deployment

Naudit ships as a container to `ghcr.io/benediktnau/naudit` and is deployed by **Coolify**.
This page covers the container setup, the full environment-variable template, automatic
deployment on each release, and what the release pipeline does internally.

Running on **Kubernetes** instead? Ready-made plain manifests live in
[`deploy/k8s/`](../deploy/k8s/README.md) and the same setup as a Helm chart in
[`deploy/helm/naudit/`](../deploy/helm/naudit) — the container facts below
(image, port, health check, env template) apply there unchanged.

For the meaning of each `Naudit:*` key see [Configuration](configuration.md).

## Container basics

- **Image:** `ghcr.io/benediktnau/naudit:latest` (or a pinned `:vX.Y.Z`).
- **Port:** the container listens on **8080** (ASP.NET default in-container). Expose/proxy `8080`.
- **Health check:** `GET /health` → `healthy` (HTTP 200). Use it as the container health probe.
- **User:** runs **non-root** (`$APP_UID`).
- **Registry access:** the ghcr package is private by default. Give Coolify a pull credential —
  a GitHub PAT with `read:packages` for `ghcr.io` — or set the package to public.

## Environment-variable template (Coolify)

The database and the WebUI are **always on** — configuration lives in the database and
is editable on the Settings page, so most of what used to be an environment variable is
now optional (see [Configuration › Where configuration lives](configuration.md#where-configuration-lives)).
Only the **bootstrap** block below is strictly required; everything else can be set here
as an override (env always wins over the database and locks that key on the Settings
page) or left for an admin to configure after first start.

```bash
# ── Bootstrap (required — env-only, read before the database exists) ───
# Naudit__Db__Provider=Sqlite                                 # Sqlite (default) | Postgres
# Naudit__Db__ConnectionString="Data Source=/data/naudit.db"  # SQLite: mount a persistent volume at /data!
# Postgres instead (no /data volume needed):
# Naudit__Db__Provider=Postgres
# Naudit__Db__ConnectionString="Host=db.example.com;Port=5432;Database=naudit;Username=naudit;Password=<secret>"  # 🔒
# Naudit__Ui__Admin__Username=admin                           # seed admin (first start, empty DB)
# Naudit__Ui__Admin__InitialPassword=<secret>                 # 🔒

# ── Everything below is optional: set here as an env override, or leave ─
# ── unset and configure on the Settings page after first start. ────────

# ── Platform selection ──────────────────────────────────────────────
Naudit__Git__Platform=GitHub          # GitHub | GitLab

# ── GitHub (when Platform=GitHub) ───────────────────────────────────
# Naudit__GitHub__BaseUrl=https://api.github.com   # only needed for GitHub Enterprise
Naudit__GitHub__Auth=Pat                           # Pat (default) | App — see docs/github-app.md
Naudit__GitHub__Token=<fine-grained-PAT>           # 🔒 secret  (Pull requests: RW, Contents: R) — only for Auth=Pat
Naudit__GitHub__WebhookSecret=<random-secret>      # 🔒 secret  (must match the repo webhook secret)
Naudit__GitHub__PostVerdict=false                  # true = post a real APPROVE/REQUEST_CHANGES review state

# ── GitHub App (recommended, instead of the Token above; requires Auth=App) ────
# Naudit__GitHub__App__AppId=123456
# Naudit__GitHub__App__PrivateKey=<base64-encoded-PEM>   # 🔒 secret
# Naudit__GitHub__App__InstallationId=                   # optional: fixed installation id

# ── GitLab (alternatively, when Platform=GitLab) ────────────────────
# Naudit__GitLab__BaseUrl=https://gitlab.example.com
# Naudit__GitLab__Token=<token-with-api-scope>     # 🔒 secret
# Naudit__GitLab__WebhookSecret=<random-secret>    # 🔒 secret  (= X-Gitlab-Token)
# Naudit__GitLab__PostVerdict=false                # true = call MR approve/unapprove from the verdict

# ── AI provider ─────────────────────────────────────────────────────
Naudit__Ai__Provider=Anthropic        # Ollama | Anthropic | OpenAICompatible
Naudit__Ai__Model=claude-sonnet-4-6
Naudit__Ai__ApiKey=<anthropic-key>    # 🔒 secret  (for Anthropic / OpenAICompatible)
# Naudit__Ai__Endpoint=               # only for Ollama (http://host:11434) or OpenAI-compatible

# ── Review (optional) ───────────────────────────────────────────────
# Naudit__Review__SystemPrompt=       # empty = built-in default prompt

# ── Access gate (optional — default Open, see docs/webui.md#access-model) ──
# Naudit__AccessGate__Mode=Registered   # Open (default) | Registered

# ── WebUI sign-in (optional — dashboard/gate work with local accounts already) ──
# Naudit__Ui__Auth__GitHub__Enabled=false        # optional self-service sign-in
# Naudit__Ui__Auth__GitHub__ClientId=
# Naudit__Ui__Auth__GitHub__ClientSecret=        # 🔒
# Naudit__Ui__Auth__Oidc__Enabled=false
# Naudit__Ui__Auth__Oidc__Authority=
# Naudit__Ui__Auth__Oidc__ClientId=
# Naudit__Ui__Auth__Oidc__ClientSecret=          # 🔒
```

> **WebUI volume:** with the default **SQLite** backend, add a persistent storage mount
> at `/data` in Coolify — the SQLite DB (settings, accounts, review history, token usage)
> lives there. With `Naudit__Db__Provider=Postgres` the data lives in your external
> database instead and no volume is needed. Session signing keys are stored in the
> database — no extra key volume.

> **Breaking (DB + UI always on):** `Naudit__Db__Enabled` and `Naudit__Ui__Enabled` have
> been **removed** — the values are now ignored, the database and the WebUI are always
> on. Deployments that previously ran **without** a database now get a SQLite file
> created at the default path on next start — **mount `/data`** (see above) or it is
> lost on redeploy. Deployments that relied on the (until now implicit) access gate must
> explicitly set `Naudit__AccessGate__Mode=Registered` — the new default is `Open`
> (every project with a valid webhook secret is reviewed, i.e. the pre-WebUI behaviour).
> Also note: the WebUI's session-cookie protection now uses a fixed Data-Protection
> application name (`SetApplicationName("Naudit")`) so it survives the host's restart
> loop — this invalidates any existing WebUI session cookies **once** on upgrade
> (users are simply asked to sign in again).

> **Breaking (pre-release rename):** deployments that used the WebUI preview must rename
> `Naudit__Ui__Db` → `Naudit__Db__ConnectionString`, `Naudit__Ui__DbProvider` →
> `Naudit__Db__Provider`, and drop `Naudit__Ui__DataProtectionKeysDir`. Data is untouched
> (same schema); active WebUI sessions are invalidated once (keys move to the database).

**AI variants** (instead of the Anthropic block):

```bash
# Ollama (local, no key):
Naudit__Ai__Provider=Ollama
Naudit__Ai__Model=llama3.1
Naudit__Ai__Endpoint=http://<ollama-host>:11434

# OpenAI-compatible (e.g. NVIDIA NIM):
Naudit__Ai__Provider=OpenAICompatible
Naudit__Ai__Endpoint=https://integrate.api.nvidia.com/v1
Naudit__Ai__Model=nvidia/llama-3.1-nemotron-ultra-253b-v1
Naudit__Ai__ApiKey=<nvidia-key>       # 🔒 secret

# Claude Code CLI (your Claude Pro/Max subscription, not an API key):
Naudit__Ai__Provider=ClaudeCode
Naudit__Ai__Model=sonnet
CLAUDE_CODE_OAUTH_TOKEN=<claude-setup-token>   # 🔒 secret — needs the derived image (see below)
```

After deploying, point the platform webhook at `https://<your-coolify-domain>/webhook/github`
(or `/webhook/gitlab`) — see [Platform setup](platform-setup.md).

## Reverse proxy / HTTPS

Coolify (Traefik) — like nginx — terminates TLS and forwards plain HTTP to the container.
Naudit honours `X-Forwarded-Proto`, so `Request.Scheme` is `https` again and the WebUI's
GitHub/OIDC login builds its OAuth `redirect_uri` as `https://…` (matching the registered
callback URL). No configuration needed — most proxies forward `X-Forwarded-Proto` by default.

By default Naudit trusts forwarded headers from **any** source (empty proxy allow-list — the
right default for containers with a dynamic proxy IP). To restrict which sources may set
`X-Forwarded-*`, pin the proxy address or subnet (empty = trust all):

```bash
Naudit__ForwardedHeaders__KnownProxies__0=10.0.0.5        # trusted proxy IP(s)
Naudit__ForwardedHeaders__KnownNetworks__0=10.0.0.0/8     # or trusted CIDR network(s)
```

## Claude Code CLI provider (subscription)

The `ClaudeCode` AI provider does not call an HTTP API — it shells out to the `claude`
CLI as a local subprocess and authenticates with a **Claude Pro/Max subscription** (no
API key, no per-token billing). The CLI must therefore live **inside the container**, so it
is **not** part of the public `ghcr.io/benediktnau/naudit` image. Use the derived image
[`deploy/coolify/Dockerfile`](../deploy/coolify/Dockerfile) — `FROM …/naudit:latest` plus the
pinned, checksum-verified `claude` binary — for your own instance only:

1. **Generate an OAuth token** once, locally, where a browser login is possible:
   ```bash
   claude setup-token   # prints a 1-year token bound to your subscription
   ```
2. **Deploy the derived image in Coolify** — set the resource's Build Pack to **Dockerfile**
   and point it at `deploy/coolify/Dockerfile` instead of the prebuilt image. Coolify rebuilds
   it on each deploy. Coolify runs a plain `docker build`, which reuses a cached `:latest`
   **base**; to also pull a fresh base each time, add `--pull` to the build command in the
   resource's build settings.
3. **Set the env vars** (in addition to the platform/webhook ones):
   ```bash
   Naudit__Ai__Provider=ClaudeCode
   Naudit__Ai__Model=sonnet                       # optional; defaults to sonnet
   CLAUDE_CODE_OAUTH_TOKEN=<token from step 1>    # 🔒 secret
   ```

**Updating the CLI:** nothing to do — each Coolify rebuild resolves the newest `stable`
release, downloads the binary, and verifies its SHA256 against the checksum in that version's
`manifest.json` (the build fails on a mismatch). BuildKit only re-downloads when the
version actually changed. To **pin or roll back** (e.g. a bad Claude Code release), build
with `--build-arg CLAUDE_CODE_VERSION=<version>` instead of the empty default.

**Caveats:**

- The token expires after **1 year** with no auto-refresh — regenerate it then.
- "Always latest" means a broken Claude Code release lands on the next deploy — use the
  `CLAUDE_CODE_VERSION` build-arg to pin back if that happens.
- Integrity is **checksum-only**: the binary is checked against the SHA256 in `manifest.json`.
  Anthropic GPG-signs that manifest (`manifest.json.sig`), but this build does not verify the
  signature — it guards against a corrupt download, not a compromised upstream.
- The derived image is ~250 MB larger than the base (the self-contained `claude` binary).

## Automatic deploy on each release

The CI does **not** deploy — Coolify owns deployment. The recommended pattern is push-based:
let the release trigger a Coolify deploy via its **webhook**.

1. In Coolify, on the Naudit resource, open **Webhooks** and copy the **deploy webhook URL**.
2. Call it after a new image is published. Either:
   - **GitHub release notification:** add a step at the end of `.github/workflows/release.yml`
     that `curl`s the webhook (store the URL as the `COOLIFY_DEPLOY_WEBHOOK` repo secret):
     ```yaml
     - name: Trigger Coolify deploy
       run: curl -fsS -X POST "${{ secrets.COOLIFY_DEPLOY_WEBHOOK }}"
     ```
   - **or** trigger it from your own tooling whenever `:latest` changes.

Coolify then re-pulls `:latest` (or the tag policy you configured) and redeploys. A pull-based
watcher (Watchtower etc.) is discouraged — it conflicts with Coolify managing its own containers.

## What the release pipeline does

`.github/workflows/release.yml` runs on push to `main` (and `workflow_dispatch`) and is a single
fail-fast job:

1. **Test gate** — `dotnet test Naudit.slnx -c Release`; red ⇒ no release, no image.
2. **Version** — `.github/scripts/next-version.sh` reads the highest `vX.Y.Z` tag and bumps the
   patch (seed `v0.1.0`). Bump major/minor manually: `git tag v0.2.0 && git push origin v0.2.0`.
3. **Build + scan + push** — the image is built and loaded locally, scanned with **Trivy**
   (fails on `CRITICAL`/`HIGH` with a fix available, `ignore-unfixed`), and only **then** pushed
   to `ghcr.io` as `vX.Y.Z`, `latest`, and `sha-<short>`.
4. **Self-contained binaries** — `Naudit.Web` is published single-file/self-contained for
   `linux-x64` and `win-x64` (bundles the .NET runtime, no install needed) and attached to the
   release as `naudit-vX.Y.Z-<rid>.{tar.gz,zip}` — see the README for the no-Docker run path.
5. **Tag + release** — pushes the git tag and creates the GitHub release with auto-generated notes
   (guarded against re-tagging an existing version; `concurrency` serializes parallel runs).

Notes:

- **`workflow_dispatch` is not a dry run** — a manual dispatch produces a real release.
- **Docs-only merges skip the release** — `paths-ignore` (`**.md`, `docs/**`) means changes that
  touch only documentation do not cut a new version or image.

## Supply-chain hardening

- **Actions pinned to immutable commit SHAs** in both workflows (not floating `@v4` tags).
- **Base images pinned by digest** (`sdk:10.0@sha256:…`, `aspnet:10.0@sha256:…`).
- **Trivy** scans the image before any push (see above).
- **Dependabot** (`.github/dependabot.yml`) keeps the pins current for three ecosystems —
  `github-actions`, `nuget`, `docker` — with a **cooldown** grace period (7 days; 14 for major)
  to dampen churn and give time to spot yanked/compromised releases, grouped into a single PR.
