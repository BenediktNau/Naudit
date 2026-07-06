# Claude Code on Coolify — derived runtime image

- **Date:** 2026-07-05
- **Status:** Implemented (2026-07-05); revised to always-latest + build-time verification
  (2026-07-06) — `deploy/coolify/Dockerfile` + `docs/deployment.md`
- **Scope:** One derived Dockerfile + one docs section. No app-code change.

## Goal

Run Naudit's `ClaudeCode` AI provider on a self-hosted Coolify deployment. That
provider (`ClaudeCodeChatClient`) shells out to the `claude` CLI as a **local
process** (`ProcessSpec FileName: "claude"`), so the CLI must live **in the same
container** as the Naudit web app — a sidecar cannot work. The CLI authenticates
against a **Claude Pro/Max subscription** via `CLAUDE_CODE_OAUTH_TOKEN` (no API
key, no per-token billing).

The operator cannot install anything on the Coolify host (no sudo). Baking the
CLI into a container image sidesteps that entirely: the build runs as root inside
the image, and Coolify just runs the container.

## Non-goals — keep the public image clean

The published image `ghcr.io/benediktnau/naudit` and the root `Dockerfile` stay
**untouched**. Other users must never get Claude Code forced on them, and the
~218 MB `claude` binary must not bloat the default image. Claude Code is added
**only** for the maintainer's own Coolify instance, via a separate image that
derives from the public one.

## Design

A new, thin **`deploy/coolify/Dockerfile`** layered on top of the public image:

```dockerfile
# syntax=docker/dockerfile:1
FROM ghcr.io/benediktnau/naudit:latest        # rolling: always the newest release
USER root

# Optional pin / rollback escape hatch: empty = newest stable at build time.
ARG CLAUDE_CODE_VERSION=

# `stable` is a tiny version-string file; BuildKit re-fetches it every build and busts the
# layer cache whenever a new version ships (no 250 MB re-download while unchanged).
ADD https://downloads.claude.ai/claude-code-releases/stable /tmp/claude-stable

# Resolve the version, read its linux-x64 SHA256 from manifest.json, download the
# self-contained native binary (own Node runtime + ripgrep) and verify it (fail-closed:
# the build aborts on a hash mismatch). curl + jq are added and removed in the same layer.
RUN set -eux; \
    apt-get update; apt-get install -y --no-install-recommends curl jq; \
    ver="${CLAUDE_CODE_VERSION:-$(cat /tmp/claude-stable)}"; \
    base="https://downloads.claude.ai/claude-code-releases/${ver}"; \
    sum="$(curl -fsSL "${base}/manifest.json" | jq -r '.platforms."linux-x64".checksum')"; \
    curl -fsSL -o /usr/local/bin/claude "${base}/linux-x64/claude"; \
    echo "${sum}  /usr/local/bin/claude" | sha256sum -c -; \
    chmod 755 /usr/local/bin/claude; \
    apt-get purge -y curl jq; apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/* /tmp/claude-stable

# The base image runs as non-root $APP_UID (1654, "app"); /home/app already exists and is
# owned by it, so just point HOME there for the CLI's state (~/.claude, ~/.claude.json).
# Disable the background auto-updater (it would try to write to /usr/local/bin and fail).
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1
USER $APP_UID
# ENTRYPOINT ["dotnet", "Naudit.Web.dll"] is inherited from the base image.
```

**Base tag:** rolling `:latest` (operator's choice). Trade-off: a Coolify rebuild
always picks up the newest Naudit release, but the base can change unnoticed
between rebuilds. Acceptable for a personal deployment.

**Install method:** always-latest with build-time SHA256 verification. Each build reads the
`stable` version pointer, pulls that version's `manifest.json`, and verifies the downloaded
`linux-x64` binary against the checksum from it (fail-closed). No version/checksum is hardcoded;
`CLAUDE_CODE_VERSION` is an optional build-arg to pin or roll back. Trade-off vs. the previous
hardcoded checksum: the hash now comes from the same source as the binary, so it still guards
against a corrupt/truncated download but no longer against a compromised upstream — acceptable
for a personal instance. curl + jq are installed and purged within the one `RUN` layer.

## Authentication & configuration (Coolify)

Generate the token once, locally, where an interactive login is possible:

```bash
claude setup-token        # OAuth flow -> prints a 1-year token bound to the subscription
```

In the Coolify service, set:

| Variable | Value | Notes |
|---|---|---|
| `CLAUDE_CODE_OAUTH_TOKEN` | *(the token)* | **Secret.** Inherited by the `claude` subprocess. Expires after 1 year — no auto-refresh; regenerate then. |
| `Naudit__Ai__Provider` | `ClaudeCode` | Selects `ClaudeCodeChatClient`. |
| `Naudit__Ai__Model` | `sonnet` | Passed to `--model`. Optional; defaults to `sonnet`. |

`ClaudeCodeChatClient` reads the token from the inherited environment. (It can
alternatively be supplied via `Naudit__Ai__ApiKey`, which the client copies into
`CLAUDE_CODE_OAUTH_TOKEN` — but the env var is cleaner and is the recommended path.)

## Deliverables

1. `deploy/coolify/Dockerfile` — the derived image above, resolving the newest `stable`
   release at build time and verifying it against the checksum in the manifest (checksum-only;
   the manifest's GPG signature is not verified).
2. A docs section (in `docs/deployment.md` or a new `docs/claude-code-coolify.md`)
   covering: why a derived image, `claude setup-token`, the Coolify env vars, and
   the token-renewal caveat.

## Verification

- `docker build -f deploy/coolify/Dockerfile -t naudit-claude .` succeeds.
- `docker run --rm --entrypoint claude naudit-claude --version` prints a version.
- End-to-end: run the container with a real `CLAUDE_CODE_OAUTH_TOKEN` +
  `Naudit__Ai__Provider=ClaudeCode`, trigger a review, confirm the CLI answers
  and a review is posted (mirrors the existing manual E2E check).

## Maintenance notes

- CLI updates are automatic: each rebuild resolves the newest `stable` release and
  verifies it. Nothing to bump.
- To pin or roll back a bad Claude Code release, build with
  `--build-arg CLAUDE_CODE_VERSION=<version>`.
- Renew `CLAUDE_CODE_OAUTH_TOKEN` yearly.
- `:latest` base means no action needed to track Naudit releases.
