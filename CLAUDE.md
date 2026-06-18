# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Naudit is a self-hosted .NET code-review bot (POC/MVP). It receives GitLab or GitHub webhooks,
has an LLM review the diff via Microsoft.Extensions.AI (MEAI), and posts a single summary
Markdown comment back to the MR/PR. Both the AI provider and the git platform are swappable
by configuration alone (`Naudit:Ai:Provider` and `Naudit:Git:Platform`).

## Commands

The solution file is `Naudit.slnx` (the XML solution format) — **not** `Naudit.sln`.
`dotnet test Naudit.sln` fails with MSB1009; always use `Naudit.slnx`.

```bash
dotnet build Naudit.slnx
dotnet test  Naudit.slnx                 # full suite

# Run the host (webhook on /webhook/gitlab|github depending on Naudit:Git:Platform, liveness on /health)
dotnet run --project src/Naudit.Web

# Single test class
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ReviewServiceTests

# Single test method
dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "FullyQualifiedName~ReviewAsync_postsModelOutput_asSummary"
```

Config secrets are set via user-secrets on the Web project, never in `appsettings.json`:

```bash
dotnet user-secrets set "Naudit:GitLab:Token" "..." --project src/Naudit.Web
```

## Architecture

Three projects with a strict, deliberate dependency direction:
`Web → Infrastructure → Core`, and `Core → MEAI abstractions only`.

- **`Naudit.Core`** — domain records (`ReviewRequest`, `CodeChange`), orchestration
  (`ReviewService`, `PromptBuilder`, `ReviewOptions`) and abstractions (`IGitPlatform`).
  **The central rule: Core depends only on `Microsoft.Extensions.AI.Abstractions`. It knows no
  concrete LLM provider and no git platform.** Everything Core needs from the outside world is
  expressed as two interfaces: `IChatClient` (from MEAI) and `IGitPlatform`. Keep provider/platform
  SDKs out of this project.
- **`Naudit.Infrastructure`** — all SDK/HTTP implementations: the AI provider factory
  (`Ai/AiClientFactory.cs`), the GitLab client (`Git/GitLab/`), and the GitHub client
  (`Git/GitHub/`). Composition lives in `DependencyInjection.cs` (`AddNauditInfrastructure`),
  which reads `Naudit:Git:Platform` and registers the matching `IGitPlatform` implementation,
  `IChatClient`, `ReviewOptions`, and `ReviewService`.
- **`Naudit.Web`** — ASP.NET Minimal API host. Only the webhook endpoint for the configured
  platform is mapped (`/webhook/gitlab` or `/webhook/github`). The endpoint validates the
  secret/signature, maps the payload, **enqueues and returns `200` immediately**, then a
  `ReviewBackgroundService` drains a `Channel`-based `ReviewQueue` and runs each review in
  its own DI scope. This avoids webhook timeouts.

  Additionally, a synchronous `POST /review` endpoint (always mapped) lets a CI/CD pipeline trigger
  a review directly instead of via webhook: it authenticates an `X-Naudit-Token` header (constant-time)
  against the active platform's `WebhookSecret`, runs the review **inline** (bypassing the queue),
  and returns `{ "verdict": "approve" | "request_changes" }` so the job can gate the merge. See
  `docs/ci-integration.md`.

### Request flow

`GitLab/GitHub webhook → /webhook/gitlab|github (validate + enqueue, 200) → ReviewQueue → ReviewBackgroundService
→ ReviewService` which: `IGitPlatform.GetChangesAsync` → `PromptBuilder.Build` → `IChatClient.GetResponseAsync`
→ `IGitPlatform.PostSummaryAsync`. If there are no changes, nothing is posted.

### Extension points (do not break the Core rule)

- **New AI provider:** add a case to the `switch` in `AiClientFactory.Create` returning an
  `IChatClient`. NVIDIA/other OpenAI-compatible endpoints reuse the OpenAI client with a custom
  `Endpoint` — no dedicated adapter. Selection is config-only via `Naudit:Ai:Provider`.
- **GitHub platform (implemented):** `src/Naudit.Infrastructure/Git/GitHub/` contains
  `GitHubPlatform` (`IGitPlatform` impl), `GitHubWebhook` (payload mapping + action filter),
  `GitHubDtos` (JSON DTOs), and `GitHubOptions` (`BaseUrl`, `Token`, `WebhookSecret`).
  Selection is config-only via `Naudit:Git:Platform` (`GitLab` | `GitHub`; default `GitLab`) —
  one platform is active per deployment; only its webhook endpoint is mapped. The GitHub endpoint
  (`/webhook/github`) verifies the `X-Hub-Signature-256` HMAC-SHA256 signature over the raw
  body (fail-closed). No change to Core.

### CI/CD & container

`Dockerfile` (repo root, multi-stage: SDK builds → ASP.NET runtime, non-root, port 8080)
containerizes the Web project. Two GitHub Actions workflows: `ci.yml` (PR gate: build + test on
`pull_request` to `main`) and `release.yml` (on push to `main` **and** `workflow_dispatch`:
test gate → `.github/scripts/next-version.sh` computes the next SemVer patch version (seed `v0.1.0`)
→ image build/push to `ghcr.io/benediktnau/naudit` (`vX.Y.Z`/`latest`/`sha-…`) → git tag + GitHub
release). `workflow_dispatch` is **not** a dry run — it performs a real release like a merge.
Deployment is done by Coolify itself; no deploy step in CI. No app-code change.

Hardening on top of that: actions pinned to commit SHAs and base images pinned by digest;
the image is Trivy-scanned (fail on CRITICAL/HIGH, `ignore-unfixed`) **before** the push;
`release.yml` has `paths-ignore` (`**.md`, `docs/**`) so docs-only merges cut no release; the
release also attaches self-contained `linux-x64`/`win-x64` single-file binaries. `.github/dependabot.yml`
tracks `github-actions`, `nuget`, and `docker` with a cooldown grace period. Deployment details and the
full Coolify env template live in `docs/deployment.md`.

## Conventions & gotchas

- **TDD workflow.** Work follows `docs/superpowers/plans/2026-06-16-naudit-codereview-bot.md`
  (11 tasks, red → green → one commit per task). Code comments are in German.
- **MEAI GA API names** are used and are version-sensitive: `IChatClient.GetResponseAsync`,
  `ChatResponse.Text`, `.AsIChatClient()` (OpenAI bridge), `new AnthropicClient(key).Messages`.
  A missing-member build error at these spots usually means a package-version mismatch, not a logic bug.
- **.NET 10 specifics:** `public partial class Program {}` is intentionally absent — the generated
  `Program` is already public, so `WebApplicationFactory<Program>` works without it (analyzer ASP0027).
- `ReviewRequest.MergeRequestIid` and the GitLab DTO `Iid` are `int` (the plan text says `long`;
  the code uses `int` because GitLab's `iid` is project-scoped and small).
- **Known cosmetic cruft:** `src/Naudit.Core/Review/PromtBuilder.cs` and
  `tests/Naudit.Tests/PromtBuilderTests.cs` have a filename typo ("Promt"); the class is correctly
  named `PromptBuilder`. `tests/Naudit.Tests/UnitTest1.cs` is a leftover xUnit template test.

## Testing approach

Core is tested with no network via `Fakes/FakeChatClient` and `Fakes/FakeGitPlatform`. Both the
GitLab and GitHub HTTP clients are tested with `Fakes/StubHttpMessageHandler` (asserts URL + body).
Webhook mapping and HMAC signature verification are covered by unit tests for both platforms.
The webhook endpoint is tested with `WebApplicationFactory<Program>` on paths that never reach the
LLM or a real git platform (401 path, non-MR/PR-event path). The real end-to-end path is verified
manually (Task 11 in the plan).
