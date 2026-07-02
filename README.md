# Naudit

**Naudit** is a self-hosted code-review bot written in .NET. It reacts to
merge-request / pull-request webhooks, has an LLM review the diff, and posts the
result back as **one** summarizing Markdown comment.

The AI provider is swappable **by configuration alone** through
[Microsoft.Extensions.AI](https://learn.microsoft.com/dotnet/ai/) (`IChatClient`) —
Anthropic (Claude), Ollama (local), or any OpenAI-compatible service (e.g. NVIDIA NIM).
The Git platform sits behind `IGitPlatform` and is likewise **config-selectable** —
GitLab or GitHub.

> Status: POC/MVP. Code complete, test suite green; productive end-to-end operation
> (a real webhook) must be wired up manually — see [Platform setup](docs/platform-setup.md).

## Features

- **Automatic MR/PR review** when a merge request (GitLab) or pull request (GitHub) is opened or updated.
- **Provider-agnostic** via MEAI — Ollama (local, no API key), Anthropic, or any OpenAI-compatible endpoint, switchable by config.
- **Platform-agnostic** — GitLab or GitHub, chosen via `Naudit:Git:Platform`, with no code change.
- **Bot identity & real reviews (optional)** — run as a **GitHub App** (`Naudit[bot]`: one-click install, one central webhook, short-lived installation tokens instead of a user PAT) and submit a **real review verdict** (`APPROVE` / `REQUEST_CHANGES`) derived from the review gate, instead of a plain comment. Opt-in and off by default; see [GitHub App setup](docs/github-app.md).
- **Asynchronous processing** — the webhook returns `200` immediately and the review runs in the background (no webhook timeout).
- **CI/CD integration** — an additional synchronous `POST /review` that returns a merge-gate verdict (see [CI integration](docs/ci-integration.md)).

## Installation & Deployment (Docker)

Naudit ships as a container to `ghcr.io/benediktnau/naudit`. Configuration is supplied
via environment variables (ASP.NET convention: `:` → `__`):

```bash
docker run -d -p 8080:8080 \
  -e Naudit__Git__Platform=GitHub \
  -e Naudit__GitHub__Token=<fine-grained-PAT> \
  -e Naudit__GitHub__WebhookSecret=<random-secret> \
  -e Naudit__Ai__Provider=Anthropic \
  -e Naudit__Ai__Model=claude-sonnet-4-6 \
  -e Naudit__Ai__ApiKey=<anthropic-key> \
  ghcr.io/benediktnau/naudit:latest

curl http://localhost:8080/health   # -> healthy
```

For the full key reference (GitLab, all provider variants, system prompt) see
[Configuration](docs/configuration.md); for connecting to GitLab/GitHub see
[Platform setup](docs/platform-setup.md).

### Bot identity & real reviews (GitHub App)

The example above uses a **user PAT** (`Naudit__GitHub__Token`) — simple, but every comment
appears as *that user*, and GitHub rejects an `APPROVE`/`REQUEST_CHANGES` verdict on the token
owner's own PRs (HTTP 422). To let Naudit act as its own bot **and** post a real, blocking review
verdict, run it as a **GitHub App**: create the app once (permissions `pull_requests: write`,
`contents: read`, `metadata: read`; subscribe to `pull_request`), then install it on a repo/org
with one click. Swap the PAT vars for:

```bash
docker run -d -p 8080:8080 \
  -e Naudit__Git__Platform=GitHub \
  -e Naudit__GitHub__Auth=App \
  -e Naudit__GitHub__App__AppId=<app-id> \
  -e Naudit__GitHub__App__PrivateKey=<base64-encoded-PEM> \
  -e Naudit__GitHub__WebhookSecret=<random-secret> \
  -e Naudit__GitHub__PostVerdict=true \
  -e Naudit__Ai__Provider=Anthropic \
  -e Naudit__Ai__Model=claude-sonnet-4-6 \
  -e Naudit__Ai__ApiKey=<anthropic-key> \
  ghcr.io/benediktnau/naudit:latest
```

`Auth=App` (default `Pat`) selects App authentication; `PostVerdict=true` (default `false`) enables
the real verdict — both are independent opt-ins, so the default behaviour is unchanged. The private
key is accepted as raw PEM or base64-encoded PEM (base64 avoids newline issues in env vars). GitLab
has no App concept; the equivalent is a group access token (bot user) plus `Naudit__GitLab__PostVerdict`.
Full walkthrough (app creation, manifest flow, install, Coolify) in [GitHub App setup](docs/github-app.md).

### Release pipeline

On every merge to `main`, `.github/workflows/release.yml` — **only when the tests are
green** — builds an image and publishes it to `ghcr.io`:

```text
ghcr.io/benediktnau/naudit:vX.Y.Z   # the release version
ghcr.io/benediktnau/naudit:latest   # always the latest main state
ghcr.io/benediktnau/naudit:sha-XXXX # exact commit (traceability)
```

**Versioning:** auto-incrementing SemVer patch from the last `vX.Y.Z` tag (seed
`v0.1.0`). Bump major/minor manually with your own tag (`git tag v0.2.0 && git push origin v0.2.0`).
**`workflow_dispatch`** is **not** a dry run — a dispatch produces a real release.
Deployment itself is handled by **Coolify** (which pulls `:latest`); CI does not deploy.
`.github/workflows/ci.yml` builds and tests every PR against `main`.

### Run without Docker (self-contained binary)

Every [release](../../releases) attaches a self-contained build that bundles the .NET
runtime — no .NET installation required. Download the archive for your platform
(`naudit-vX.Y.Z-linux-x64.tar.gz` or `naudit-vX.Y.Z-win-x64.zip`), extract it, and run
the executable; configure it with the same environment variables as the container:

```bash
tar -xzf naudit-vX.Y.Z-linux-x64.tar.gz
Naudit__Git__Platform=GitHub Naudit__GitHub__Token=... ./Naudit.Web --urls http://localhost:8080
curl http://localhost:8080/health   # -> healthy
```

### Build from source

```bash
git clone <REPO-URL> naudit && cd naudit
dotnet build Naudit.slnx          # restores NuGet packages automatically
dotnet test  Naudit.slnx          # test suite — should be green
dotnet run --project src/Naudit.Web --urls http://localhost:5080
```

> The solution file is `Naudit.slnx` (XML format), **not** `Naudit.sln`.
> Prerequisite: [.NET SDK 10](https://dotnet.microsoft.com/download).

## Documentation

- [Configuration](docs/configuration.md) — all `Naudit:*` keys, secrets, choosing an AI provider
- [Deployment](docs/deployment.md) — Coolify env template, auto-deploy, release pipeline & supply-chain hardening
- [Platform setup](docs/platform-setup.md) — wiring up the GitLab/GitHub webhook + simulating a review locally
- [GitHub App setup](docs/github-app.md) — bot identity (`Naudit[bot]`), one-click install, and real `APPROVE`/`REQUEST_CHANGES` reviews
- [CI integration](docs/ci-integration.md) — synchronous `POST /review` as a merge gate
- [Architecture & status](docs/architecture.md) — how it works, project layout, tests, roadmap, known limitations

## License

[MIT](LICENSE) © 2026 Benedikt Nau
