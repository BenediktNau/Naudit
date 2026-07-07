# WebUI — access gate & dashboard

Naudit ships an optional web UI that serves two purposes:

1. **Access gate.** Reviews only run for projects that belong to an *active* account.
   Webhooks from anyone else are silently dropped (HTTP 200 to the platform, a log line
   inside). This makes it safe to make the GitHub App **public** without strangers burning
   your LLM tokens. The synchronous `POST /review` endpoint returns an honest `403` instead.
2. **Dashboard.** Token usage (from MEAI `ChatResponse.Usage`), projects (auto-registered
   on their first review), recently reviewed PRs with per-finding detail, an admin
   approvals view and a per-user profile.

Everything sits behind **`Naudit:Ui:Enabled` (default `false`)** — switched off, Naudit
behaves exactly as before: no gate, no database, no UI endpoints.

Screens: login, dashboard, approvals (admin), settings (admin, **read-only**), profile.
UI design source of truth: `Naudit WebUI.dc.html` (Claude Design project). Dark-only,
Space Grotesk + Space Mono, one green accent (`#4ADE80`).

## Enabling (Coolify / environment)

```bash
Naudit__Ui__Enabled=true
Naudit__Ui__DbProvider=Sqlite                    # Sqlite (default) | Postgres
Naudit__Ui__Db="Data Source=/data/naudit.db"     # SQLite: /data = persistent volume!
Naudit__Ui__Admin__Username=admin                # seed admin (created on first start, empty DB)
Naudit__Ui__Admin__InitialPassword=<secret>      # 🔒

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
there. Without it, accounts and review history are lost on every redeploy.

### Postgres instead of SQLite

For a shared/managed database, set `Naudit:Ui:DbProvider=Postgres` and point `Naudit:Ui:Db`
at a Npgsql connection string — no `/data` volume needed then:

```bash
Naudit__Ui__DbProvider=Postgres
Naudit__Ui__Db="Host=db.example.com;Port=5432;Database=naudit;Username=naudit;Password=<secret>"  # 🔒
```

Both backends share **one** schema and the same startup `Database.Migrate()` — the initial
migration is kept provider-neutral, so switching is config-only (a fresh database is created
and migrated on first start). Nothing else changes.

## Access model

| Path | Status after sign-in | Who approves |
| --- | --- | --- |
| **Local user** (created by admin in *Approvals → Add user*) | `Active` immediately — creation *is* the approval | — |
| **GitHub OAuth** (self-service, opt-in) | `Pending` | admin approves in *Approvals* |
| **OIDC/Keycloak** (self-service, opt-in) | `Pending` | admin approves in *Approvals* |

The gate matches the **owner** part of a project id (`owner/repo` → `owner`; GitLab
numeric project ids match as a whole) against the **GitHub links** of active accounts:

- GitHub OAuth sign-in fills the user's own login automatically.
- For local and OIDC accounts the admin maintains the links (*Approvals → Links*).
  Accounts without any link are flagged (`no GitHub link`) — their repos are **not**
  reviewed until a link is set.

Unauthorized webhooks are dropped silently; the project simply gets no review comment.

## Settings are read-only

The Settings screen shows the *effective* configuration (AI provider/model, git platform,
bot identity, PostVerdict, sign-in methods, whether a custom system prompt is configured) —
**it never edits anything and returns no secrets**. Configuration stays env/appsettings-only
by design; see [Configuration](configuration.md).

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

Naudit reads Keycloak's `preferred_username` as the account name and the `sub` claim as
the stable external id.

## Architecture notes

- **BFF pattern:** the SPA never sees a token — HttpOnly session cookie (`naudit.session`),
  `GET /api/me` resolves the auth state, 401 on `/api/*` sends the SPA back to the login.
- **Frontend** lives in `src/frontend` (Vite + React + TS + Tailwind 4, NauAssist layout);
  the container build compiles it into `wwwroot/`. For local dev: `dotnet run` (port 5290)
  + `npm run dev` (proxies `/api` and `/auth`).
- **Persistence** is EF Core (`src/Naudit.Infrastructure/Data/`) on **SQLite (default) or
  Postgres** (`Naudit:Ui:DbProvider`); the schema is applied via `Database.Migrate()` at
  startup (only when the UI is enabled). The `InitialUi` migration is hand-kept
  provider-neutral (no explicit column types, both identity strategies annotated) so a single
  migration runs on either backend; on Postgres EF's pending-changes check is suppressed
  (the committed model snapshot is SQLite-flavoured). A Postgres round-trip is covered by the
  opt-in `NauditDbContextPostgresTests` (runs only when `NAUDIT_TEST_POSTGRES` is set).
- **Core seams:** `IAccessGate` (gate check) and `IReviewAuditSink` (review + token-usage
  recording) — both no-ops when the UI is off. Sink failures never fail a review.
