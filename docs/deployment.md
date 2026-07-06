# Deployment

Naudit ships as a container to `ghcr.io/benediktnau/naudit` and is deployed by **Coolify**.
This page covers the container setup, the full environment-variable template, automatic
deployment on each release, and what the release pipeline does internally.

For the meaning of each `Naudit:*` key see [Configuration](configuration.md).

## Container basics

- **Image:** `ghcr.io/benediktnau/naudit:latest` (or a pinned `:vX.Y.Z`).
- **Port:** the container listens on **8080** (ASP.NET default in-container). Expose/proxy `8080`.
- **Health check:** `GET /health` → `healthy` (HTTP 200). Use it as the container health probe.
- **User:** runs **non-root** (`$APP_UID`).
- **Registry access:** the ghcr package is private by default. Give Coolify a pull credential —
  a GitHub PAT with `read:packages` for `ghcr.io` — or set the package to public.

## Environment-variable template (Coolify)

Configuration is supplied via environment variables (ASP.NET convention: `:` → `__`).
Add these in Coolify; mark the 🔒 ones as secrets.

```bash
# ── Platform selection ──────────────────────────────────────────────
Naudit__Git__Platform=GitHub          # GitHub | GitLab

# ── GitHub (when Platform=GitHub) ───────────────────────────────────
# Naudit__GitHub__BaseUrl=https://api.github.com   # only needed for GitHub Enterprise
Naudit__GitHub__Token=<fine-grained-PAT>           # 🔒 secret  (Pull requests: RW, Contents: R)
Naudit__GitHub__WebhookSecret=<random-secret>      # 🔒 secret  (must match the repo webhook secret)

# ── GitLab (alternatively, when Platform=GitLab) ────────────────────
# Naudit__GitLab__BaseUrl=https://gitlab.example.com
# Naudit__GitLab__Token=<token-with-api-scope>     # 🔒 secret
# Naudit__GitLab__WebhookSecret=<random-secret>    # 🔒 secret  (= X-Gitlab-Token)

# ── AI provider ─────────────────────────────────────────────────────
Naudit__Ai__Provider=Anthropic        # Ollama | Anthropic | OpenAICompatible
Naudit__Ai__Model=claude-sonnet-4-6
Naudit__Ai__ApiKey=<anthropic-key>    # 🔒 secret  (for Anthropic / OpenAICompatible)
# Naudit__Ai__Endpoint=               # only for Ollama (http://host:11434) or OpenAI-compatible

# ── Review (optional) ───────────────────────────────────────────────
# Naudit__Review__SystemPrompt=       # empty = built-in default prompt
```

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
