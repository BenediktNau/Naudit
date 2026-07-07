# Design: Database as a first-class Naudit concern (decouple from WebUI)

**Date:** 2026-07-07
**Status:** Designed, approved. Pending spec review + implementation plan (writing-plans).
**Branch (spec committed on):** `feat/webui`

## Problem / motivation

Today the whole persistence layer is gated behind `Naudit:Ui:Enabled` and its config lives under
`Naudit:Ui:Db` / `Naudit:Ui:DbProvider`. But the database backs more than the dashboard: the access
gate (`EfAccessGate`), the review audit log (`EfReviewAuditSink`), accounts, and — once we add it —
the Data-Protection keys. Naming it "the UI database" is misleading; it is **Naudit's database**.

Trigger: on Postgres the Data-Protection keys are currently only in-memory (the file-based persistence
auto-derives a directory only for SQLite), so every container restart invalidates all WebUI session
cookies. The keys should live in the database instead — which only makes sense once the DB is framed
as a first-class concern rather than a UI detail.

## Goals

- Make the database an independent concern (`Naudit:Db`), decoupled from the WebUI.
- Access gate + audit log can run **without** the dashboard SPA.
- Persist Data-Protection keys in the database (replacing the file-based persistence entirely).
- Keep the Core rule intact (DB/DataProtection stay Web/Infrastructure concerns; Core untouched).
- Keep the single migration story provider-neutral (SQLite + Postgres from one migration set).

## Non-goals

- No change to the review flow, AI providers, or git platforms.
- No encryption-at-rest for the DP keys (the "No XML encryptor" warning stays; acceptable for a
  self-hosted single instance).
- No data migration of existing file-based DP keys (feature is new/unreleased on `feat/webui`).

## Decisions

- **Section name:** `Naudit:Db` (terse, matches the existing `DbProvider` naming). *(Default choice;
  revisit vs. `Naudit:Database` before implementation if desired.)*
- **PR shape:** one cohesive PR — "DB as a first-class Naudit concern (decouple from UI, rename config,
  DP keys in DB)". The rename/decouple is the base; DP-keys-in-DB is a small addition on top. *(Default
  choice; could be split into decouple+rename first, then DP keys.)*
- **`Enabled` is explicit** (default `false`), not inferred from connection-string presence, because
  the connection string has a default value (`Data Source=/data/naudit.db`).

## Design

### 1. Config shape

New standalone `Naudit:Db` section replacing the UI-scoped DB keys:

| old | new |
| --- | --- |
| `Naudit__Ui__Db` | `Naudit__Db__ConnectionString` |
| `Naudit__Ui__DbProvider` | `Naudit__Db__Provider` |
| `Naudit__Ui__DataProtectionKeysDir` | **removed** (keys → DB) |
| — | `Naudit__Db__Enabled` (new, default `false`) |

- New `DatabaseOptions { bool Enabled; UiDbProvider Provider; string ConnectionString }`.
  - `ConnectionString` default stays `"Data Source=/data/naudit.db"` (SQLite).
  - The `UiDbProvider` enum (`Sqlite`/`Postgres`) moves/renames alongside (e.g. `DbProvider`); pick a
    home in `Naudit.Infrastructure` (Data or a small Options file) during implementation.
- `UiOptions` keeps only `Enabled`, `Admin`, `Admins`, `Auth`. Its `Db`, `DbProvider`,
  `DataProtectionKeysDir` properties and `ResolveDataProtectionKeysDir()` are removed.

### 2. DI wiring (`DependencyInjection.cs`) — two independent switches, one dependency

- `Naudit:Db:Enabled=true` → register `NauditDbContext` (provider switch: `UseNpgsql` /
  `UseSqlite`, keep the Postgres `PendingModelChangesWarning` suppression), `EfAccessGate`,
  `EfReviewAuditSink`. Else `AllowAllAccessGate` + `NullReviewAuditSink` (today's "off" behaviour).
- `Naudit:Ui:Enabled=true` → dashboard SPA, auth, `AccountService`.
- **UI ⇒ DB dependency:** `Ui:Enabled=true` without `Db:Enabled=true` is invalid → **fail fast at
  startup** with a clear message (same pattern as the existing `Auth=App` without `App:AppId`
  check). Access gate/audit log without a dashboard is supported; UI without a DB is not.

### 3. Data-Protection keys → DB

- Add package `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (Infrastructure; add to Web
  too if the `PersistKeysToDbContext` extension is not visible transitively).
- `NauditDbContext : IDataProtectionKeyContext` with `DbSet<DataProtectionKey> DataProtectionKeys`.
- `Program.cs`: replace `PersistKeysToFileSystem(...)` with `PersistKeysToDbContext<NauditDbContext>()`;
  delete the `ResolveDataProtectionKeysDir()` block. Registration stays under `Ui:Enabled` (only
  cookie-auth needs keys, and UI ⇒ DB, so the context is always present).
- **New migration `AddDataProtectionKeys`** (do NOT edit `InitialUi` — deployed DBs already applied it).
  Keep it **provider-neutral** exactly like the existing tables: no explicit column types, both
  `Sqlite:Autoincrement` and `Npgsql:ValueGenerationStrategy` annotated in `Up()` and in the Designer's
  `BuildTargetModel`, and re-neutralize the regenerated model snapshot after `dotnet ef migrations add`.

### 4. Startup (`Program.cs`)

- Run `Database.MigrateAsync()` under `if (dbOptions.Enabled)` (migrate whenever the DB is on).
- Run the seed-admin under `if (uiOptions.Enabled)` (accounts only matter with the UI).
- Auth middleware and UI endpoints stay under `if (uiOptions.Enabled)`.

### 5. Docs + cleanup

- Update `docs/webui.md`, `docs/deployment.md`, `docs/configuration.md`, and `CLAUDE.md` to the new
  env names; remove the `Naudit__Ui__DataProtectionKeysDir` guidance.
- Scan for any `appsettings*.json` references to the old keys.
- Mark the `rename-ui-db-config` memory note as fulfilled.

## Testing

- `NauditDbContextPostgresTests` (Postgres round-trip, gated on `NAUDIT_TEST_POSTGRES`) covers the new
  `DataProtectionKeys` table via the shared migration.
- New tests:
  - Fail-fast when `Ui:Enabled && !Db:Enabled`.
  - DB-on-without-UI ⇒ `EfAccessGate` + `EfReviewAuditSink` are active, no auth middleware / UI endpoints.
- Existing webhook/endpoint tests continue to pass with the config rename.

## Open questions (non-blocking, defaults chosen above)

- `Naudit:Db` vs `Naudit:Database` as the section name.
- One cohesive PR vs. decouple+rename then DP-keys.

## Next steps

1. Spec review (Benedikt).
2. `writing-plans` → implementation plan (TDD, red → green → one commit per task).
