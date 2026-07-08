# WebUI — access gate & dashboard

Naudit's database and web UI are **always on** — they are also where configuration
itself lives now (see [Configuration › Where configuration lives](configuration.md#where-configuration-lives)).
The UI serves three purposes:

1. **Access gate.** With `Naudit:AccessGate:Mode=Registered`, reviews only run for
   projects that belong to an *active* account. Webhooks from anyone else are silently
   dropped (HTTP 200 to the platform, a log line inside). This makes it safe to make the
   GitHub App **public** without strangers burning your LLM tokens. The synchronous
   `POST /review` endpoint returns an honest `403` instead. The default mode, `Open`,
   reviews every project with a valid webhook secret (the pre-WebUI behaviour) — see
   [Access model](#access-model) below.
2. **Dashboard.** Token usage (from MEAI `ChatResponse.Usage`), projects (auto-registered
   on their first review), recently reviewed PRs with per-finding detail, an admin
   approvals view and a per-user profile.
3. **Settings.** An admin can view and edit the DB-managed configuration (platform, AI
   provider, review/gate tuning, sign-in providers, access-gate mode) without a redeploy
   — see [Settings are editable](#settings-are-editable) below.

Screens: login, dashboard, approvals (admin), settings (admin, editable), profile.
UI design source of truth: `Naudit WebUI.dc.html` (Claude Design project). Dark-only,
Space Grotesk + Space Mono, one green accent (`#4ADE80`).

## Enabling (Coolify / environment)

The database and UI need no switch anymore — only the bootstrap keys below are required;
everything else (including the access-gate mode and sign-in providers) can instead be
left unset here and configured on the Settings page after first start.

```bash
Naudit__Db__Provider=Sqlite                                 # Sqlite (default) | Postgres
Naudit__Db__ConnectionString="Data Source=/data/naudit.db"  # SQLite: /data = persistent volume!
Naudit__Ui__Admin__Username=admin                # seed admin (created on first start, empty DB)
Naudit__Ui__Admin__InitialPassword=<secret>      # 🔒

# Optional: only needed to restrict reviews to registered accounts (default is Open)
# Naudit__AccessGate__Mode=Registered

# Optional self-service sign-in (both default off — local login always works):
Naudit__Ui__Auth__GitHub__Enabled=true
Naudit__Ui__Auth__GitHub__ClientId=<oauth-app-client-id>
Naudit__Ui__Auth__GitHub__ClientSecret=<secret>  # 🔒
Naudit__Ui__Auth__Oidc__Enabled=true
Naudit__Ui__Auth__Oidc__Authority=https://keycloak.example.com/realms/main
Naudit__Ui__Auth__Oidc__ClientId=naudit
Naudit__Ui__Auth__Oidc__ClientSecret=<secret>    # 🔒

# Optional: usernames that get the admin role on sign-in (external providers)
# Naudit__Ui__Admins__0=BenediktNau
```

**Important (SQLite):** mount a persistent volume at `/data` — the SQLite database lives
there. Without it, settings, accounts and review history are lost on every redeploy.

### Postgres instead of SQLite

For a shared/managed database, set `Naudit:Db:Provider=Postgres` and point `Naudit:Db:ConnectionString` at a Npgsql connection string — no `/data` volume needed then:

```bash
Naudit__Db__Provider=Postgres
Naudit__Db__ConnectionString="Host=db.example.com;Port=5432;Database=naudit;Username=naudit;Password=<secret>"  # 🔒
```

Both backends share **one** schema and the same startup `Database.Migrate()` — the initial
migration is kept provider-neutral, so switching is config-only (a fresh database is created
and migrated on first start). Nothing else changes.

### Running without ever touching the UI

The database and the dashboard endpoints are always there, but nothing forces you to use
them: with the default `Naudit:AccessGate:Mode=Open`, every project with a valid webhook
secret is reviewed and the review audit log fills up in the background — you never need
to sign in. Switching to `Registered` (or wanting the dashboard/token-usage view) is when
an admin account and the Settings/Approvals screens become relevant.

Session-cookie signing keys (ASP.NET Data Protection) are stored **in the database** on
both backends, so sessions survive container restarts — no key directory or extra volume.

## Access model

Only relevant when `Naudit:AccessGate:Mode=Registered` (the default, `Open`, reviews
every project with a valid webhook secret and ignores accounts/links entirely).

| Path | Status after sign-in | Who approves |
| --- | --- | --- |
| **Local user** (created by admin in *Approvals → Add user*) | `Active` immediately — creation *is* the approval | — |
| **GitHub OAuth** (self-service, opt-in) | `Pending` | admin approves in *Approvals* |
| **OIDC/Keycloak** (self-service, opt-in) | `Pending` | admin approves in *Approvals* |

In `Registered` mode, the gate matches the **owner** part of a project id (`owner/repo` →
`owner`; GitLab numeric project ids match as a whole) against the **GitHub links** of
active accounts:

- GitHub OAuth sign-in fills the user's own login automatically.
- For local and OIDC accounts the admin maintains the links (*Approvals → Links*).
  Accounts without any link are flagged (`no GitHub link`) — their repos are **not**
  reviewed until a link is set.

On GitHub-App deployments (`Naudit:GitHub:Auth=App`), a signed-in user whose linked GitHub
account/org has not installed the Naudit app yet sees an **install banner** on the dashboard and
the pending screen that links to the app's install page; see
[GitHub App setup](github-app.md#install-from-the-naudit-webui). The banner reflects the live
installation state and clears itself once the app is installed.

Unauthorized webhooks are dropped silently; the project simply gets no review comment.

## Settings are editable

The Settings screen shows every DB-managed key (platform, AI provider/model, review/gate
tuning, access-gate mode, sign-in methods — the whitelist in
[`SettingsCatalog.cs`](../src/Naudit.Infrastructure/Settings/SettingsCatalog.cs)) and lets
an **admin** change them:

- **Environment always wins.** A key set via user-secrets/environment shows up **locked**
  ("via environment") and cannot be edited here — that deployment's env value keeps
  applying regardless of what the Settings page shows.
- **Changes apply after a restart.** Saving writes to the database and shows a
  "restart required" banner with a "Restart now" button (`IAppRestarter` — an in-process
  restart, no container/orchestrator restart needed, a couple of seconds of downtime).
- **Secrets are write-only.** The API never returns a secret's value, only whether it is
  set; a new value overwrites, an empty value clears it back to the default. Secrets are
  encrypted at rest (ASP.NET Data Protection) — see
  [Configuration › Where configuration lives](configuration.md#where-configuration-lives)
  for the honest caveat on what that protects against.
- A broken configuration (e.g. `Auth=App` without a private key) does not crash-loop the
  container — the host starts in a **recovery mode** instead: webhooks and `POST /review`
  are disabled, the error is shown on the Settings page, and login/UI stay available so an
  admin can fix it and restart.

## Provider setup

**GitHub OAuth App** (Settings → Developer settings → OAuth Apps → New):

| Setting | Value |
| --- | --- |
| Homepage URL | `https://<your-host>` |
| Authorization callback URL | `https://<your-host>/auth/callback/github` |

**Keycloak client** (confidential, Standard Flow):

| Setting | Value |
| --- | --- |
| Client ID | `naudit` (or your choice — must match `Oidc:ClientId`) |
| Valid redirect URIs | `https://<your-host>/auth/callback/oidc` |
| Client authentication | On (client secret → `Oidc:ClientSecret`) |

> **HTTPS behind a reverse proxy.** The callback URLs are served over HTTPS by your proxy.
> Naudit trusts `X-Forwarded-Proto`, so the generated `redirect_uri` matches the registered
> `https://…` URL out of the box (Coolify/Traefik, nginx). If login fails with a `redirect_uri`
> mismatch, check that the proxy forwards that header — see
> [Deployment › Reverse proxy](deployment.md#reverse-proxy--https).

Naudit reads Keycloak's `preferred_username` as the account name and the `sub` claim as
the stable external id.

## Architecture notes

- **BFF pattern:** the SPA never sees a token — HttpOnly session cookie (`naudit.session`),
  `GET /api/me` resolves the auth state, 401 on `/api/*` sends the SPA back to the login.
- **Frontend** lives in `src/frontend` (Vite + React + TS + Tailwind 4, NauAssist layout);
  the container build compiles it into `wwwroot/`. For local dev: `dotnet run` (port 5290)
  + `npm run dev` (proxies `/api` and `/auth`).
- **Persistence** is EF Core (`src/Naudit.Infrastructure/Data/`) on **SQLite (default) or
  Postgres** (`Naudit:Db:Provider`) and is always on; the schema is applied via
  `Database.Migrate()` at startup (run once, in the `DbSettingsLoader` bootstrap, before
  the host itself is built). The migrations (`InitialUi`, `AddDataProtectionKeys`, `AddSettingsAndSetupDraft`,
  which adds the `Settings` and `SetupDraft` tables) are hand-kept
  provider-neutral (no explicit column types, both identity strategies annotated) so a
  single migration chain runs on either backend; on Postgres EF's pending-changes check
  is suppressed (the committed model snapshot is SQLite-flavoured). Data-Protection keys
  live in the `DataProtectionKeys` table (`PersistKeysToDbContext`) under a fixed
  application name (`"Naudit"`) shared by the bootstrap loader and the host, so settings
  encrypted by one are readable by the other. A Postgres round-trip is covered by the
  opt-in `NauditDbContextPostgresTests` (runs only when `NAUDIT_TEST_POSTGRES` is set).
- **Core seams:** `IAccessGate` (gate check — `AllowAllAccessGate` in `Open` mode,
  `EfAccessGate` in `Registered` mode) and `IReviewAuditSink` (`EfReviewAuditSink`; review
  + token-usage recording). Sink failures never fail a review.
- **Config model:** the database is also a `IConfiguration` source (`NauditConfig.InsertDbSettings`,
  inserted right after `appsettings.json` and below user-secrets/env — see
  [Configuration › Where configuration lives](configuration.md#where-configuration-lives)),
  loaded and decrypted at bootstrap by `DbSettingsLoader` before the host is built. A
  `Program.cs` host loop (`IAppRestarter`) rebuilds the host in-process when the Settings
  page requests a restart, so DB config changes apply without a container restart.
