# Naudit WebUI — design spec

Date: 2026-07-07 · Status: approved (brainstormed with Benedikt, visual design refined in
Claude Design: `Naudit WebUI.dc.html` in design project `ce71197e-eb68-4745-9c61-d45452571913`)

## Goal

A web UI served by the existing Naudit host that

1. **gates the use of Naudit**: reviews only run for projects that belong to an *active*
   account — this makes it safe to make the GitHub App public without strangers burning
   LLM tokens;
2. provides a **dashboard**: token usage, projects (auto-registered on first review),
   recently reviewed PRs with findings detail;
3. supports **admin-managed access**: admin creates local users (active immediately) or
   approves self-service sign-ups (GitHub OAuth / OIDC, both opt-in) that start `pending`.

## Non-goals (v1)

- **No settings editing.** The Settings screen renders the *effective* configuration
  read-only. The system prompt, provider choice, PostVerdict etc. remain config-only
  (`appsettings` / env vars) exactly as today. The mockup's prompt text is **not** adopted
  anywhere. (Explicit instruction: "der momentane SystemPrompt soll erstmal so bleiben".)
- No billing/cost calculation — token counts only.
- No GitLab-specific UI wiring (the gate is platform-neutral via `ProjectId`; the
  GitHub-link auto-fill applies to GitHub logins only).
- No historical backfill: usage/reviews are recorded from the moment the feature ships.

## Architecture

Folder structure follows NauAssist; the .NET projects keep Naudit's existing layout.

```
src/
  frontend/                      # NEW — Vite + React 19 + TS + Tailwind 4 + TanStack Query
    src/api/                     #   fetch client per resource + types.ts
    src/components/pages/        #   LoginPage, DashboardPage, ApprovalsPage, SettingsPage, ProfilePage
    src/components/ui/           #   Button, StatTile, Pill, Panel, Toggle(read-only), …
    src/hooks/queries.ts         #   TanStack Query hooks
    src/lib/auth.tsx             #   BFF AuthGate (401 → login redirect), like NauAssist
  Naudit.Core/
    Abstractions/IAccessGate.cs        # NEW seam
    Abstractions/IReviewAuditSink.cs   # NEW seam
  Naudit.Infrastructure/
    Data/                        # NEW — NauditDbContext (EF Core + SQLite), entities, migrations
    Ui/                          # NEW — gate/sink/account services implementations
  Naudit.Web/
    Endpoints/                   # NEW — AuthEndpoints, ApiEndpoints (dashboard/projects/reviews/accounts/settings/usage)
```

Deployment stays **one container**: multi-stage Dockerfile gains a Node stage that builds
`src/frontend` → `dist/` is copied to `wwwroot/` (NauAssist pattern). ASP.NET serves the
SPA statically with a fallback to `index.html` for non-`/api`, non-`/webhook` routes.

## Auth (BFF, cookie session)

- **Local login is the normal path and always on.** Admin creates users
  (username/password); those accounts are **active immediately** (creation *is* the
  approval). Passwords hashed with ASP.NET Core `PasswordHasher<T>` (no full Identity).
- **GitHub OAuth: opt-in** (`Naudit:Ui:Auth:GitHub:Enabled`, ClientId/Secret). Self-service
  sign-in creates a `Pending` account; the GitHub login is auto-added to the account's
  GitHub links.
- **OIDC/Keycloak: opt-in** (`Naudit:Ui:Auth:Oidc:Enabled`, Authority/ClientId/Secret).
  Self-service sign-in creates a `Pending` account; no automatic GitHub link (flagged in
  the approvals UI, admin adds the link).
- Frontend never sees a token: HttpOnly cookie, `GET /api/me` resolves state, any 401 on
  `/api/*` redirects to the login page (NauAssist's fetch-wrapper pattern).
- Disabled providers disappear from the login page entirely (login B variant in the design).
- **Seed admin:** on startup, if the accounts table is empty, create an active admin from
  `Naudit:Ui:Admin:Username` + `Naudit:Ui:Admin:InitialPassword`. Additionally any account
  whose username is listed in `Naudit:Ui:Admins` gets the admin role on sign-in.

## Access gate

- Core seam: `IAccessGate.IsAllowedAsync(string projectId, CancellationToken)`.
- Semantics: owner segment of `owner/repo` (case-insensitive) must match a GitHub link of
  an account with `Status = Active`. (GitLab project ids pass through the same lookup —
  a link entry simply holds the value that matches the platform's `ProjectId` owner.)
- Enforced in **both** entry points *before* work happens: webhook endpoints (before
  enqueue; unauthorized ⇒ log + HTTP 200 + drop, silent to the outside) and `POST /review`
  (unauthorized ⇒ 403 with a JSON error, since the caller is the operator's own CI).
- **Feature flag:** `Naudit:Ui:Enabled` (default `false`). Off ⇒ no UI endpoints, no gate
  (exactly today's behaviour), no DB required. On ⇒ UI + gate + persistence active.

## Review audit sink

- Core seam: `IReviewAuditSink.RecordAsync(ReviewAudit audit, CancellationToken)` with
  `ReviewAudit(ProjectId, MergeRequestIid, Title, Verdict, Summary, Findings, InputTokens,
  OutputTokens, Model)` — a Core record; token counts come from MEAI's
  `ChatResponse.Usage` (already a Core-legal dependency).
- Called in `ReviewService.ReviewAsync` after `PostReviewAsync`. Sink failures are logged,
  never fail the review. Default registration when UI is off: `NullReviewAuditSink`.
- The EF implementation upserts the `projects` row (auto-registration on first review,
  linking it to the owning account via the gate's match) and inserts `reviews` +
  `review_findings`.

## Data model (SQLite via EF Core)

| Table | Columns (essentials) |
| --- | --- |
| `accounts` | Id, Username (unique), PasswordHash?, Provider (`Local\|GitHub\|Oidc`), ExternalId?, DisplayName?, Status (`Pending\|Active\|Rejected`), IsAdmin, CreatedAt |
| `account_github_links` | Id, AccountId FK, Login (owner/org, unique per account) |
| `projects` | Id, PlatformProjectId (unique), AccountId FK?, FirstReviewedAt, LastReviewedAt |
| `reviews` | Id, ProjectId FK, PrNumber, Title, Verdict, Summary, InputTokens, OutputTokens, Model?, CreatedAt |
| `review_findings` | Id, ReviewId FK, Severity, Confidence, File?, Line?, Text |

- Connection string default `Data Source=/data/naudit.db`, overridable via
  `Naudit:Ui:Db`. `/data` is a Coolify volume. A committed EF migration is applied on
  startup via `Database.Migrate()` (only when `Ui:Enabled=true`).
- DbContext + entities live in Infrastructure (`Data/`); Core stays SDK-free.

## API surface (all cookie-authenticated unless noted)

| Endpoint | Who | Purpose |
| --- | --- | --- |
| `POST /auth/login` | anon | local username/password → cookie |
| `GET /auth/login/github`, `/auth/login/oidc` | anon | challenge redirect (only when enabled) |
| `POST /auth/logout` | user | end session |
| `GET /api/me` | anon | `{ isAuthenticated, username, isAdmin, status, authProviders }` (drives login page + AuthGate) |
| `GET /api/dashboard` | active user | stat tiles + per-day series (tokens, reviews) + project/review lists (admin: all; user: own projects) |
| `GET /api/projects` | active user | projects incl. recent PRs per project |
| `GET /api/reviews/{id}` | active user | summary + findings detail (the expandable row) |
| `GET /api/accounts` | admin | pending + approved lists |
| `POST /api/accounts` | admin | create local user (active immediately) |
| `POST /api/accounts/{id}/approve` / `reject` / `revoke` | admin | status transitions |
| `PUT /api/accounts/{id}/github-links` | admin | maintain owner/org links |
| `GET /api/settings` | admin | **read-only** effective config: AI provider+model, git platform, auth methods on/off, GitHub-App/PostVerdict flags; secrets masked, system prompt shown as "built-in default" or "custom (configured)" — never editable |
| `GET /api/me/usage` | active user | profile stats: monthly token bars, totals, per-project usage |

Pending users get a dedicated "pending" screen after login (only `/api/me` works for them).

## Visual design

Source of truth: `Naudit WebUI.dc.html` (Claude Design). Key tokens:

- Colors: `--bg #0D1117`, `--surface #141A22`, `--elev #1A222C`, `--border #242D38`,
  ink `#E6EDF3/#98A5B3/#5F6B78`, accent `#4ADE80` (hover `#22C55E`), teal `#53D3D1`
  (links/info), warn `#E3B341`, danger `#F47067`. Dark-only, deliberately.
- Fonts: **Space Grotesk** (sans) + **Space Mono** (mono, headings/numbers/data),
  self-hosted via `@fontsource` (no runtime CDN).
- UI copy: **English**.
- Screens: login (providers per config), dashboard (3 stat tiles, 2 with area sparkline;
  expandable project rows → PR list; expandable review rows → verdict, model, token in/out,
  summary, severity-tagged findings with `file:line`), approvals (pending/approved, add
  user, approve/reject/revoke, "no GitHub link" flag), settings (read-only, see non-goals),
  profile `/me` (monthly bars, totals, per-project bars).
- Note: the design's review detail shows PR author/branch/diffstat — `ReviewRequest`
  doesn't carry these today; v1 renders what exists (title, number, verdict, tokens,
  model, findings) and omits author/branch/diffstat rather than extending the webhook
  mapping now.

## Config keys (new, all under `Naudit:Ui`)

```
Naudit:Ui:Enabled                  false   # master switch: UI + gate + persistence
Naudit:Ui:Db                       Data Source=/data/naudit.db
Naudit:Ui:Admin:Username           —       # seed admin (first start, empty DB)
Naudit:Ui:Admin:InitialPassword    —       # 🔒
Naudit:Ui:Admins                   []      # usernames that get admin role
Naudit:Ui:Auth:GitHub:Enabled      false
Naudit:Ui:Auth:GitHub:ClientId/ClientSecret      # 🔒
Naudit:Ui:Auth:Oidc:Enabled        false
Naudit:Ui:Auth:Oidc:Authority/ClientId/ClientSecret  # 🔒
```

## CI / container

- `Dockerfile`: add Node build stage (pin digest), copy `dist/` → `wwwroot/`.
- `ci.yml`: add frontend job (npm ci, typecheck, lint, build) alongside dotnet test.
- `release.yml`: unchanged flow; the image build picks up the new stage; Trivy still gates.

## Testing

- **Backend** (`tests/Naudit.Tests`): gate unit tests (owner matching, inactive/pending,
  flag off ⇒ allow); audit-sink tests (records review + findings, upserts project, failure
  is swallowed); endpoint tests via `WebApplicationFactory` + SQLite in-memory: local
  login (wrong/right password), 401 redirect contract, admin-only authz (403 for
  non-admin), account lifecycle (create/approve/revoke), webhook drop for unauthorized
  project, `/review` 403, `/api/me`, `/api/settings` masking.
- **Frontend**: `tsc --noEmit`, `eslint`, `vite build` in CI (NauAssist level; no
  component tests in v1).
- TDD per repo convention; German code comments; docs in English.

## Rollout

1. Ship with `Ui:Enabled=false` — zero behaviour change, no DB needed.
2. Enable on Coolify: set `Ui:Enabled=true`, mount `/data`, set seed admin + (optionally)
   OAuth/OIDC secrets, redeploy.
3. Sign in as admin, add own GitHub owner links → own repos keep being reviewed; everything
   else is now gated.
