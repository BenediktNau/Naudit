# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

Naudit is a self-hosted .NET code-review bot (POC/MVP). It receives GitLab or GitHub webhooks,
has an LLM review the diff via Microsoft.Extensions.AI (MEAI), and posts inline comments on the
changed lines plus one summary comment back to the MR/PR. Both the AI provider and the git
platform are swappable by configuration alone (`Naudit:Ai:Provider` and `Naudit:Git:Platform`).

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

# WebUI frontend (src/frontend — Vite + React + TS + Tailwind 4, NauAssist layout)
cd src/frontend && npm ci && npm run lint && npm run build   # build = tsc --noEmit && vite build
npm run dev   # dev server, proxies /api and /auth to dotnet run (port 5290)
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
→ ReviewService` which: `IGitPlatform.GetChangesAsync` → (optional SAST/SCA grounding **and** repo-context enrichment, one shared checkout) →
`IPromptRedactor.RedactAsync` (mask secrets/IPs/e-mails in diff + findings + title, **before** the prompt) →
`PromptBuilder.Build` → `IChatClient.GetResponseAsync` → `IGitPlatform.PostReviewAsync`. If there are no
changes, nothing is posted. The merge verdict is **derived** from a severity-aware gate over the LLM
findings' severity/confidence (the LLM no longer returns a top-level verdict); see `docs/review-gate.md`.
`PostReviewAsync` now carries that derived `ReviewVerdict` as a parameter (the only Core type crossing
the seam); actually **posting** it as a real, blocking review state is opt-in via `Naudit:GitHub:PostVerdict`
/ `Naudit:GitLab:PostVerdict` (default `false` = today's behaviour: GitHub `event="COMMENT"`, GitLab posts
no approve/unapprove call). The git-API token is resolved **per request** from the review's `ProjectId` via
`IGitTokenProvider` (async — `ResolveTokenAsync`, since minting can be I/O) (per-project override, else the
global token) — set on each `HttpRequestMessage`, not as a static default header; see
`docs/configuration.md#per-project-tokens`.

### Extension points (do not break the Core rule)

- **New AI provider:** add a case to the `switch` in `AiClientFactory.Create` returning an
  `IChatClient`. NVIDIA/other OpenAI-compatible endpoints reuse the OpenAI client with a custom
  `Endpoint` — no dedicated adapter. Selection is config-only via `Naudit:Ai:Provider`.
- **GitHub platform (implemented):** `src/Naudit.Infrastructure/Git/GitHub/` contains
  `GitHubPlatform` (`IGitPlatform` impl), `GitHubWebhook` (payload mapping + action filter),
  `GitHubDtos` (JSON DTOs), and `GitHubOptions` (`BaseUrl`, `Token`, `WebhookSecret`, `ProjectTokens`,
  `Auth`, `App`, `PostVerdict`). Selection is config-only via `Naudit:Git:Platform` (`GitLab` | `GitHub`;
  default `GitLab`) — one platform is active per deployment; only its webhook endpoint is mapped. The
  GitHub endpoint (`/webhook/github`) verifies the `X-Hub-Signature-256` HMAC-SHA256 signature over
  the raw body (fail-closed). No change to Core.
- **New SAST/SCA analyzer:** implement `ISastAnalyzer` in
  `src/Naudit.Infrastructure/Sast/`, map the tool's output to `ScanFinding`, and
  add a `case` in the analyzer-selection `switch` in `DependencyInjection.cs`.
  Selection is config-only via `Naudit:Sast:Analyzers`. No change to Core. The
  findings are fed to the LLM as grounding (`PromptBuilder`); the verdict stays
  LLM-driven — derived from the LLM findings' own severity/confidence via the
  severity-aware gate (`Naudit:Review:Gate`, see `docs/review-gate.md`), never from
  SAST findings.
- **New prompt redactor:** implement `IPromptRedactor` in
  `src/Naudit.Infrastructure/Redaction/` and register it in `DependencyInjection.cs`.
  The interface lives in Core (`Naudit.Core.Abstractions`), the implementation in
  Infrastructure (Core rule intact). The default `PatternRedactor` (regex + entropy)
  masks secrets/IPs/e-mails **before** `PromptBuilder.Build`; `Naudit:Redaction:Enabled=false`
  swaps in `NullPromptRedactor` (no-op). Seam for a future Presidio/LLM redactor. See
  `docs/redaction.md`.
- **Repo-context collector:** `IContextCollector` (Core `Abstractions`) cuts surrounding
  code, call-sites of changed symbols, and a repo overview from the shared workspace
  checkout; the default `WorkspaceContextCollector`
  (`src/Naudit.Infrastructure/Context/`) is language-agnostic (regex + indentation).
  Collected context is redacted like the diff and rendered by `PromptBuilder` as a
  read-only "Repository context" section. On by default; `Naudit:Review:Context:Enabled=false`
  restores the diff-only prompt. Seam for a future Roslyn/tree-sitter or cached "repo map"
  collector — just another impl + registration. See `docs/review-context.md`.
- **DB + WebUI, always on; config lives in the DB:** the `Naudit:Db:Enabled`/
  `Naudit:Ui:Enabled` switches are gone — `NauditDbContext`, the access gate, the review
  audit log, and the WebUI/BFF-auth are unconditional. What used to gate the access check
  is now an explicit `Naudit:AccessGate:Mode` (`Open` default — every project with a valid
  webhook secret is reviewed, the pre-WebUI behaviour; `Registered` — `EfAccessGate`, only
  projects of active accounts), selected in `DependencyInjection.cs` between
  `AllowAllAccessGate` and `EfAccessGate`. `IReviewAuditSink` has one impl now
  (`EfReviewAuditSink`, called after `PostReviewAsync` with verdict/findings/token usage
  from MEAI `ChatResponse.Usage`; failures never fail the review) — both are still the
  `IPromptRedactor`-pattern Core seams (`IAccessGate` checked in both webhook endpoints
  before enqueue — silent drop with 200 — and in `POST /review` — 403).

  **Config model:** most `Naudit:*` keys are now DB-managed — a whitelist in
  `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` (`SettingDefinition(Key,
  IsSecret)`; list-shaped keys like `ProjectTokens`/`Ui:Admins` and the admin seed stay
  env-only on purpose). `SettingsService` writes/removes rows in the new `Settings` table,
  encrypting `IsSecret` values with Data Protection (purpose `"Naudit.Settings"`).
  `DbSettingsLoader.Load` runs **before the host is built**: it builds its own throwaway
  `ServiceProvider` (DbContext + Data Protection on the same DB), runs
  `Database.Migrate()`, reads and decrypts the `Settings` rows (an undecryptable secret —
  keyring gone — is treated as missing, not a crash) and hands back a plain
  `Dictionary<string,string?>`. `NauditConfig.InsertDbSettings` inserts that dictionary as
  a `MemoryConfigurationSource` right after the last `appsettings*.json` source and returns
  the sources still above it as `EnvOverrides` (used by the Settings API to tell "set via
  env, locked" from "set via DB"): `appsettings.json < DB-Settings < user-secrets/env/
  command-line`. `AddNauditInfrastructure` and all `*Options` classes are unchanged — they
  still just read `IConfiguration` and don't know a value came from the DB. The bootstrap
  loader and the host share one fixed Data-Protection application name
  (`DbSettingsLoader.DataProtectionAppName = "Naudit"`, `SetApplicationName`) so values
  encrypted by one are decryptable by the other — **this is a breaking one-time change**:
  it invalidates pre-existing WebUI session cookies on upgrade.

  **Host restart loop:** `Program.cs` wraps host build+run in a `while(true)` loop around
  a single `AppRestarter` (`IAppRestarter`: `RequestRestart` stops the current host via
  `IHostApplicationLifetime`, `MarkRestartPending`/`RestartPending` drive the Settings
  page's "restart required" banner). After `RunAsync` returns, the loop reads
  `ConsumeRestartRequest()`, disposes the outgoing `WebApplication` (else a DI container
  leaks per restart), and either rebuilds (config changes from the DB now apply) or exits.
  `PUT /api/settings` writes to the DB and marks a pending restart;
  `POST /api/settings/restart` triggers it — no container/orchestrator restart needed.

  **Recovery mode:** the host build probes `AddNauditInfrastructure` against a throwaway
  `ServiceCollection` first; if that throws (e.g. `GitHub:Auth=App` without a private key),
  the real host still comes up but with the review pipeline registrations, the webhook
  endpoints, and `POST /review` skipped — only `/health` and the always-mapped WebUI
  (login, Settings, showing the error via `StartupState.RecoveryError`) stay live, so an
  admin can fix the config and restart instead of crash-looping.

  Persistence lives in `src/Naudit.Infrastructure/Data/` (EF Core; `Database.Migrate()`
  now runs inside `DbSettingsLoader`, before the host, not in `Program.cs`). ASP.NET
  Data-Protection keys are persisted **in the DB** (`NauditDbContext` implements
  `IDataProtectionKeyContext`) — sessions survive restarts on both backends. The
  migrations (`InitialUi`, `AddDataProtectionKeys`, and the new `Settings`/`SetupDraft`
  tables) are hand-kept provider-neutral: no explicit column types, both
  `Sqlite:Autoincrement` and `Npgsql:ValueGenerationStrategy` annotated in `Up()`, no
  `HasColumnType` in the Designers; on Postgres the `PendingModelChangesWarning` is
  suppressed. A future `dotnet ef migrations add` re-bakes SQLite types — re-neutralize
  the new migration + Designer (snapshot stays SQLite-baked). Postgres round-trip: opt-in
  `NauditDbContextPostgresTests`, gated on `NAUDIT_TEST_POSTGRES`. BFF-auth + JSON API in
  `src/Naudit.Web/Endpoints/` (cookie session, 401 instead of redirects; `SettingsEndpoints`
  is the editable Settings API — GET returns catalog+source+lock state, PUT validates
  then writes, secrets never round-trip); the React SPA in `src/frontend/` is compiled
  into `wwwroot/` by the container build. See `docs/webui.md` and `docs/configuration.md`.

  This (DB Pflicht, config-in-DB, restart loop, recovery mode, `AccessGate:Mode`) is the
  "Fundament" slice of a larger design; see
  `docs/superpowers/specs/2026-07-08-setup-wizard-design.md` for the full plan. The
  first-run **setup wizard** is now built on top of it: `SetupStatus`
  (`src/Naudit.Infrastructure/Setup/SetupStatus.cs`) checks the effective config against
  the required key set per platform/provider at host build time; missing ⇒ **setup
  mode** — webhooks, `POST /review`, and the review pipeline stay unmapped, but the whole
  WebUI base (login, Settings, Dashboard) stays up. `/api/setup/*`
  (`src/Naudit.Web/Endpoints/SetupEndpoints.cs`) is the wizard API (admin creation guarded
  Grafana-style — only while no admin exists — then draft/test-ai/apply), backed by
  `SetupDraftService` (a single DP-encrypted `SetupDraft` row) and rendered by the React
  `SetupGate`/`SetupWizard` (`src/frontend/src/components/setup/`) ahead of the AuthGate.
  The **platform automation** (PR 3) is now complete too: `POST /api/setup/github/manifest`
  builds the GitHub App manifest and the browser form-POSTs it, `GET /api/setup/github/manifest-callback`
  (anonymous, state-bound) exchanges the returned code into the draft, and `POST /api/setup/gitlab/hooks`
  creates GitLab webhooks per target (idempotent) — HTTP in setup mode runs through the new
  `SetupHttpClientFactory` seam (no `IHttpClientFactory` before `AddNauditInfrastructure`).
- **Per-project git token:** `IGitTokenProvider` (`src/Naudit.Infrastructure/Git/`) resolves the
  git-API token from the review's `ProjectId` (per-project override → global fallback) via
  `ResolveTokenAsync` (async — implementations may mint tokens over HTTP, not just look them up).
  The default `ConfiguredGitTokenProvider` reads a `ProjectTokens` list (a list, not a map, so
  `owner/repo` stays in the *value* and is env-var-settable) from the active platform's config
  section; the platform clients apply it **per request** (GitHub `Authorization: Bearer`, GitLab
  `PRIVATE-TOKEN`). No change to Core (tokens are an Infrastructure concern). See
  `docs/configuration.md#per-project-tokens`.
- **GitHub App auth (second `IGitTokenProvider` impl):** `GitHubAppTokenProvider`
  (`src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs`) mints short-lived GitHub App
  installation tokens (App-JWT RS256 → `GET .../installation` lookup → `POST .../access_tokens`),
  cached until ~5 min before expiry. Selected config-only via `Naudit:GitHub:Auth = Pat | App`
  (default `Pat` = `ConfiguredGitTokenProvider`, today's behaviour) in the `AddNauditInfrastructure`
  GitHub branch, which fails fast at startup if `Auth=App` is set without `App:AppId`/`App:PrivateKey`.
  Same seam as the per-project provider above — a further secret-store-backed `IGitTokenProvider`
  (Vault/Key Vault/DB) is just another impl + registration. See `docs/github-app.md`.
  On WebUI deployments (`Naudit:Ui:Enabled=true`) the App mode also drives an **install-onboarding
  banner**: `GET /api/me/github-app` (`src/Naudit.Web/Endpoints/GitHubAppEndpoints.cs`, mapped only
  when `Platform=GitHub` **and** `Auth=App`) uses `GitHubAppInstallationChecker`
  (`src/Naudit.Infrastructure/Git/GitHub/`, sharing the App-JWT via the extracted `GitHubAppJwt`) to
  live-check `GET /users/{login}/installation` (org fallback) per linked login (result cached ~5 min
  per login) and derive the install
  deep-link from the app slug (`GET /app`); fail-quiet (API error ⇒ `installed: null`, no banner).
  The SPA renders the banner on the dashboard + pending screen and a status row on the profile.
- **Author sessions (bring your own subscription):** `IAiClientRouter` (Core `Abstractions`)
  selects the chat client per review; `SingleClientRouter` (default, feature off) returns the
  global `IChatClient`, `AuthorSessionRouter` (`src/Naudit.Infrastructure/Ai/ClaudeCode/`) routes
  MRs to the author's own Claude subscription (token stored DP-encrypted per account, profile
  page/`/api/me/claude-session`), wrapped in `FallbackChatClient` (any author failure ⇒ in-memory
  cooldown + one retry on the global client). Toggle `Naudit:Ai:AuthorSessions:Enabled`
  (default `false` = today's behaviour). See `docs/author-sessions.md`.

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
