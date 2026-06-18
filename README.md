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
- [Platform setup](docs/platform-setup.md) — wiring up the GitLab/GitHub webhook + simulating a review locally
- [CI integration](docs/ci-integration.md) — synchronous `POST /review` as a merge gate
- [Architecture & status](docs/architecture.md) — how it works, project layout, tests, roadmap, known limitations

## License

[MIT](LICENSE) © 2026 Benedikt Nau
