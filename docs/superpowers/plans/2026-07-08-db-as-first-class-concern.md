# DB as a First-Class Naudit Concern — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Decouple the database from the WebUI (`Naudit:Ui:Db*` → new standalone `Naudit:Db` section) and persist the Data-Protection keys in the database instead of the file system.

**Architecture:** A new `DatabaseOptions` (`Naudit:Db:Enabled|Provider|ConnectionString`) drives DbContext + `EfAccessGate` + `EfReviewAuditSink` registration; `Naudit:Ui:Enabled` only drives dashboard/auth/`AccountService` and **requires** the DB (fail-fast at startup). `NauditDbContext` implements `IDataProtectionKeyContext`; a new hand-neutralized provider-neutral migration `AddDataProtectionKeys` adds the keys table, and `Program.cs` swaps `PersistKeysToFileSystem` for `PersistKeysToDbContext<NauditDbContext>()`.

**Tech Stack:** .NET 10, EF Core 10.0.9 (SQLite + Npgsql), `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` 10.0.9, xUnit + `WebApplicationFactory<Program>`.

**Spec:** `docs/superpowers/specs/2026-07-07-db-as-first-class-concern-design.md`
**Branch:** implement on `feat/webui` (the WebUI is unreleased there, so the breaking env rename is acceptable; Benedikt's Coolify deployment must switch env vars on next deploy — see Task 4).

## Global Constraints

- Solution file is `Naudit.slnx` — `dotnet build Naudit.slnx` / `dotnet test Naudit.slnx`. **Never** `Naudit.sln`.
- Code comments in **German**; docs (`docs/**`, `README.md`) in **English**.
- TDD: red → green → **one commit per task**. Commit trailer: `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- Core rule: `Naudit.Core` depends only on MEAI abstractions — DB and DataProtection stay in Infrastructure/Web. Nothing in this plan touches `src/Naudit.Core`.
- Do **not** edit the existing `InitialUi` migration (`20260707170820_InitialUi.cs` / `.Designer.cs`) — deployed DBs already applied it.
- Migrations are hand-kept **provider-neutral**: in `Up()` no `type:` arguments, every PK-Id carries **both** `.Annotation("Sqlite:Autoincrement", true)` and `.Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)`; in the `.Designer.cs` all `.HasColumnType(...)` calls are removed. The model snapshot (`NauditDbContextModelSnapshot.cs`) stays SQLite-baked as EF generates it (that is the existing convention; the Postgres `PendingModelChangesWarning` suppression in `DependencyInjection.cs` covers it).
- New config keys: `Naudit:Db:Enabled` (default `false`), `Naudit:Db:Provider` (`Sqlite` default | `Postgres`), `Naudit:Db:ConnectionString` (default `Data Source=/data/naudit.db`). Removed keys: `Naudit:Ui:Db`, `Naudit:Ui:DbProvider`, `Naudit:Ui:DataProtectionKeysDir`.

## File Structure

| File | Responsibility |
| --- | --- |
| `src/Naudit.Infrastructure/Data/DatabaseOptions.cs` (new) | `Naudit:Db` config record: `Enabled`, `Provider` (new `DbProvider` enum), `ConnectionString` |
| `src/Naudit.Infrastructure/Ui/UiOptions.cs` | Slimmed to UI-only concerns: `Enabled`, `Admin`, `Admins`, `Auth` (drops `Db`, `DbProvider`, `DataProtectionKeysDir`, `ResolveDataProtectionKeysDir()`, `UiDbProvider`) |
| `src/Naudit.Infrastructure/DependencyInjection.cs` | Two independent switches: `Db:Enabled` → DbContext/gate/sink, `Ui:Enabled` → `AccountService`; fail-fast UI⇒DB |
| `src/Naudit.Infrastructure/Data/NauditDbContext.cs` | Implements `IDataProtectionKeyContext` (new `DataProtectionKeys` DbSet) |
| `src/Naudit.Infrastructure/Data/Migrations/<ts>_AddDataProtectionKeys.cs` + `.Designer.cs` (new, generated then neutralized) | Provider-neutral `DataProtectionKeys` table |
| `src/Naudit.Web/Program.cs` | Migrate under `Db:Enabled`, seed under `Ui:Enabled`, `PersistKeysToDbContext` instead of file keys |
| `tests/Naudit.Tests/DatabaseOptionsTests.cs` (new) | Defaults of `DatabaseOptions` |
| `tests/Naudit.Tests/DbWiringTests.cs` (new) | DI wiring of the two switches + fail-fast + "DB without UI" endpoint behaviour |
| Docs (`webui.md`, `deployment.md`, `configuration.md`, `CLAUDE.md`) | New env names, DP-keys-in-DB story |

---

### Task 1: `DatabaseOptions` + config rename + UI/DB decoupling

**Files:**
- Create: `src/Naudit.Infrastructure/Data/DatabaseOptions.cs`
- Create: `tests/Naudit.Tests/DatabaseOptionsTests.cs`
- Create: `tests/Naudit.Tests/DbWiringTests.cs`
- Modify: `src/Naudit.Infrastructure/Ui/UiOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs:153-186`
- Modify: `src/Naudit.Web/Program.cs` (lines 4, 49-57, 132-140)
- Modify: `src/Naudit.Web/appsettings.json`
- Modify: `tests/Naudit.Tests/UiOptionsTests.cs`
- Modify (mechanical key rename): `tests/Naudit.Tests/AccessGateEndpointTests.cs`, `AdminEndpointTests.cs`, `AuthEndpointTests.cs`, `ExternalAuthTests.cs`, `SpaHostingTests.cs`, `DataEndpointTests.cs`

**Interfaces:**
- Consumes: existing `NauditDbContext`, `EfAccessGate`, `EfReviewAuditSink`, `AllowAllAccessGate`, `NullReviewAuditSink`, `AccountService`, `UiOptions`.
- Produces: `public enum DbProvider { Sqlite, Postgres }` and `public sealed class DatabaseOptions { bool Enabled; DbProvider Provider; string ConnectionString }` in namespace `Naudit.Infrastructure.Data`; `DatabaseOptions` registered as singleton in DI (Task 3's `Program.cs` code and the docs rely on these names). After this task, DP keys are temporarily **in-memory** (file persistence removed; Task 3 adds DB persistence).

- [ ] **Step 1: Write the failing tests**

Create `tests/Naudit.Tests/DatabaseOptionsTests.cs`:

```csharp
using Naudit.Infrastructure.Data;
using Xunit;

namespace Naudit.Tests;

public class DatabaseOptionsTests
{
    [Fact]
    public void Defaults_disabled_sqlite_volumeDbPath()
    {
        var o = new DatabaseOptions();
        Assert.False(o.Enabled);
        Assert.Equal(DbProvider.Sqlite, o.Provider);
        Assert.Equal("Data Source=/data/naudit.db", o.ConnectionString);
    }
}
```

Create `tests/Naudit.Tests/DbWiringTests.cs` (the `Build` helper mirrors `GitTokenWiringTests`):

```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

/// <summary>Verdrahtung der zwei Schalter: Naudit:Db:Enabled (DbContext + Gate + Audit-Sink)
/// und Naudit:Ui:Enabled (Dashboard/Auth/Accounts) — DB ohne UI ist gültig, UI ohne DB nicht.</summary>
public class DbWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public DbWiringTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DbOn_registersEfGateAndSink()
    {
        using var sp = Build(new()
        {
            ["Naudit:Db:Enabled"] = "true",
            ["Naudit:Db:ConnectionString"] = "Data Source=unused.db",
        });
        using var scope = sp.CreateScope();
        Assert.IsType<EfAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<EfReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public void DbOff_registersNoOps()
    {
        using var sp = Build(new());
        using var scope = sp.CreateScope();
        Assert.IsType<AllowAllAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<NullReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public async Task DbOnUiOff_uiEndpointsNotMapped()
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-dbwiring-{Guid.NewGuid():N}.db")}";
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
            b.UseSetting("Naudit:Db:Enabled", "true");
            b.UseSetting("Naudit:Db:ConnectionString", db);
        }).CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode); // Host läuft & DB migriert
    }
}
```

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "DatabaseOptionsTests|DbWiringTests"`
Expected: **build error** — `DatabaseOptions` / `DbProvider` do not exist yet. (Compile-red counts as red here; the rename cannot be staged compilably.)

- [ ] **Step 3: Create `DatabaseOptions`**

Create `src/Naudit.Infrastructure/Data/DatabaseOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Data;

/// <summary>DB-Backend der Persistenz. Ein gemeinsames Schema/eine Migrationskette für beide
/// (die Migrationen sind provider-neutral handgepflegt).</summary>
public enum DbProvider { Sqlite, Postgres }

/// <summary>Config-Section Naudit:Db — Naudits Persistenz als eigenständiger Belang
/// (Zugangsschranke, Audit-Log, Accounts, Data-Protection-Keys), von der UI entkoppelt.
/// Enabled ist EXPLIZIT (Default false) statt aus dem ConnectionString abgeleitet,
/// weil der ConnectionString einen Default-Wert hat.</summary>
public sealed class DatabaseOptions
{
    public bool Enabled { get; set; }

    /// <summary>DB-Backend: SQLite (Default, /data-Volume) oder Postgres (externe DB).</summary>
    public DbProvider Provider { get; set; } = DbProvider.Sqlite;

    /// <summary>Connection-String für das gewählte Backend:
    /// SQLite <c>Data Source=/data/naudit.db</c> (Default; /data liegt auf einem Volume) bzw.
    /// Postgres <c>Host=…;Database=…;Username=…;Password=…</c>.</summary>
    public string ConnectionString { get; set; } = "Data Source=/data/naudit.db";
}
```

- [ ] **Step 4: Slim `UiOptions` down to UI-only concerns**

In `src/Naudit.Infrastructure/Ui/UiOptions.cs`, delete the `UiDbProvider` enum (lines 3-5), the `DbProvider`, `Db`, `DataProtectionKeysDir` properties and the whole `ResolveDataProtectionKeysDir()` method (lines 13-43). Update the class doc comment. Resulting head of the file (rest — `SeedAdminOptions` etc. — unchanged):

```csharp
namespace Naudit.Infrastructure.Ui;

/// <summary>Config-Section Naudit:Ui — WebUI (Dashboard, Auth, Accounts). Opt-in:
/// Enabled=false (Default) ⇒ keine UI-Endpoints, kein Auth. Setzt die DB voraus
/// (Naudit:Db:Enabled — UI ⇒ DB, s. DependencyInjection).</summary>
public sealed class UiOptions
{
    public bool Enabled { get; set; }

    /// <summary>Seed-Admin: wird beim Start angelegt, wenn die Accounts-Tabelle leer ist.</summary>
    public SeedAdminOptions Admin { get; set; } = new();

    /// <summary>Usernames, die beim (externen) Sign-in automatisch Admin werden.</summary>
    public List<string> Admins { get; set; } = new();

    public UiAuthOptions Auth { get; set; } = new();
}
```

- [ ] **Step 5: Rewire `DependencyInjection.cs`**

Replace the whole WebUI block (`src/Naudit.Infrastructure/DependencyInjection.cs:153-186`, from the `// WebUI (Naudit:Ui): …` comment through the `else { … NullReviewAuditSink … }` block) with:

```csharp
        // Persistenz (Naudit:Db): eigenständiger Belang — DbContext + Zugangsschranke + Audit-Sink
        // nur bei Enabled, sonst No-Ops (= Verhalten ohne DB, keine DB-Datei nötig).
        // Beide Options immer registrieren, damit Program.cs/Endpoints sie lesen können.
        var dbOptions = configuration.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton(dbOptions);
        var uiOptions = configuration.GetSection("Naudit:Ui").Get<UiOptions>() ?? new UiOptions();
        services.AddSingleton(uiOptions);

        if (dbOptions.Enabled)
        {
            // Backend per Config; dieselbe (provider-neutrale) Migrationskette läuft auf beiden.
            services.AddDbContext<NauditDbContext>(o =>
            {
                switch (dbOptions.Provider)
                {
                    case DbProvider.Postgres:
                        o.UseNpgsql(dbOptions.ConnectionString);
                        // Der committete Model-Snapshot ist SQLite-geprägt (Migrations werden gegen
                        // SQLite geschrieben); auf Postgres zeigt EFs Pending-Changes-Prüfung deshalb
                        // einen gutartigen, konventionsbedingten Diff (Identity-Strategie). Nur hier
                        // unterdrücken — auf SQLite bleibt die Warnung als „Migration vergessen?"-Netz aktiv.
                        o.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                        break;
                    default:
                        o.UseSqlite(dbOptions.ConnectionString);
                        break;
                }
            });
            services.AddScoped<IAccessGate, EfAccessGate>();
            services.AddScoped<IReviewAuditSink, EfReviewAuditSink>();
        }
        else
        {
            services.AddSingleton<IAccessGate>(new AllowAllAccessGate());
            services.AddSingleton<IReviewAuditSink>(new NullReviewAuditSink());
        }

        // WebUI (Naudit:Ui): nur Accounts/Dashboard-Belange — braucht die DB (UI ⇒ DB).
        if (uiOptions.Enabled)
        {
            services.AddScoped<AccountService>();
        }
```

(The `using Naudit.Infrastructure.Data;` needed for `DatabaseOptions`/`DbProvider` is already present at line 12.)

- [ ] **Step 6: Update `Program.cs`**

Three edits in `src/Naudit.Web/Program.cs`:

1. Delete line 4 (`using Microsoft.AspNetCore.DataProtection;`) — unused after edit 2.
2. Delete the Data-Protection file-persistence block (lines 49-57: the comment `// Data-Protection-Keys persistieren, …` through the closing `}` after `PersistKeysToFileSystem`). Keys are in-memory until Task 3 puts them in the DB.
3. Replace the startup migration/seed block (lines 132-140) with:

```csharp
// Persistenz: Migration immer, wenn die DB an ist; Seed-Admin nur mit UI (Accounts = UI-Belang).
var dbOptions = app.Services.GetRequiredService<Naudit.Infrastructure.Data.DatabaseOptions>();
if (dbOptions.Enabled)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Data.NauditDbContext>();
    await db.Database.MigrateAsync(); // async im async-Startup — kein Thread-Pool-Blocking
    if (uiConfig.Enabled)
        await scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>().SeedAsync();
}
```

(The old block read `UiOptions` into a local `uiOptions` used only there — the replacement uses the existing `uiConfig` from line 23 instead, so that local disappears.)

- [ ] **Step 7: Declare the new section in `appsettings.json`**

In `src/Naudit.Web/appsettings.json`, insert before the `"Ui"` object:

```json
    "Db": {
      "Enabled": false
    },
```

- [ ] **Step 8: Update the test config keys (mechanical)**

In each file below, replace every `b.UseSetting("Naudit:Ui:Db", db);` line with two lines at the same indentation:

```csharp
b.UseSetting("Naudit:Db:Enabled", "true");
b.UseSetting("Naudit:Db:ConnectionString", db);
```

Occurrences: `AccessGateEndpointTests.cs:52`, `AdminEndpointTests.cs:23` and `:42`, `AuthEndpointTests.cs:23`, `ExternalAuthTests.cs:20`, `SpaHostingTests.cs:22` (16-space indent), `DataEndpointTests.cs:28` and `:150`.

Then rewrite `tests/Naudit.Tests/UiOptionsTests.cs` (the `Db` default moved to `DatabaseOptionsTests`):

```csharp
using Naudit.Infrastructure.Ui;
using Xunit;

namespace Naudit.Tests;

public class UiOptionsTests
{
    [Fact]
    public void Defaults_disabled_noExternalAuth()
    {
        var o = new UiOptions();
        Assert.False(o.Enabled);
        Assert.False(o.Auth.GitHub.Enabled);
        Assert.False(o.Auth.Oidc.Enabled);
        Assert.Empty(o.Admins);
    }
}
```

- [ ] **Step 9: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS (all tests, including the three new ones). If `DbOnUiOff_uiEndpointsNotMapped` fails on `/health`, the startup migration block is not running under `Db:Enabled` — recheck Step 6.

- [ ] **Step 10: Grep for leftovers**

Run: `grep -rn "Naudit:Ui:Db\|Naudit__Ui__Db\|UiDbProvider\|ResolveDataProtectionKeysDir" src tests`
Expected: no matches. (Docs still match — Task 4.)

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(db): Naudit:Db als eigenständige Config-Sektion — DB von der UI entkoppelt

Naudit:Ui:Db/DbProvider -> Naudit:Db:ConnectionString/Provider, neues explizites
Naudit:Db:Enabled. DbContext + EfAccessGate + EfReviewAuditSink hängen an der DB,
AccountService an der UI; Migration läuft bei aktiver DB auch ohne Dashboard.
Datei-Persistenz der Data-Protection-Keys entfällt (Keys kommen im Folge-Commit in die DB).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 2: Fail-fast when the UI is on but the DB is off

**Files:**
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (inside the block added in Task 1)
- Modify: `tests/Naudit.Tests/DbWiringTests.cs`

**Interfaces:**
- Consumes: `DatabaseOptions`/`UiOptions` singletons and the `Build` helper from Task 1.
- Produces: `AddNauditInfrastructure` throws `InvalidOperationException` (message contains `Naudit:Db:Enabled`) when `Ui:Enabled && !Db:Enabled` — same pattern as the existing `Auth=App` without `App:AppId` check at `DependencyInjection.cs:51-52`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Naudit.Tests/DbWiringTests.cs`:

```csharp
    [Fact]
    public void UiOn_dbOff_failsFastAtStartup()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(new()
        {
            ["Naudit:Ui:Enabled"] = "true",
            // Naudit:Db:Enabled fehlt absichtlich
        }));
        Assert.Contains("Naudit:Db:Enabled", ex.Message);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~UiOn_dbOff_failsFastAtStartup"`
Expected: FAIL — no exception is thrown (registration currently succeeds without a DbContext).

- [ ] **Step 3: Implement the fail-fast**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, directly after the two `AddSingleton(dbOptions)`/`AddSingleton(uiOptions)` lines from Task 1 Step 5, insert:

```csharp
        // UI ⇒ DB: ohne DbContext gäbe es erst beim ersten Request kryptische DI-Fehler —
        // lieber sofort beim Start scheitern (gleiches Muster wie Auth=App ohne AppId).
        if (uiOptions.Enabled && !dbOptions.Enabled)
            throw new InvalidOperationException(
                "Naudit:Ui:Enabled=true verlangt Naudit:Db:Enabled=true (die UI braucht Naudits Datenbank).");
```

- [ ] **Step 4: Run the full suite**

Run: `dotnet test Naudit.slnx`
Expected: PASS — the new test is green, and every UI test still passes because Task 1 Step 8 set `Naudit:Db:Enabled=true` wherever the UI is on.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(db): Fail-fast beim Start, wenn UI ohne DB aktiviert ist

Naudit:Ui:Enabled=true ohne Naudit:Db:Enabled=true wirft InvalidOperationException
beim Start (gleiches Muster wie Auth=App ohne AppId) statt DI-Fehler beim ersten Request.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 3: Data-Protection keys → DB (`PersistKeysToDbContext` + migration `AddDataProtectionKeys`)

**Files:**
- Modify: `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`
- Modify: `src/Naudit.Infrastructure/Data/NauditDbContext.cs`
- Create (generated, then neutralized): `src/Naudit.Infrastructure/Data/Migrations/<timestamp>_AddDataProtectionKeys.cs` + `.Designer.cs`
- Modify (regenerated by the tool): `src/Naudit.Infrastructure/Data/Migrations/NauditDbContextModelSnapshot.cs`
- Modify: `src/Naudit.Web/Program.cs`
- Modify: `tests/Naudit.Tests/NauditDbContextTests.cs`, `tests/Naudit.Tests/AuthEndpointTests.cs`, `tests/Naudit.Tests/NauditDbContextPostgresTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext`, `DatabaseOptions` wiring from Task 1; `AuthEndpointTests.CreateApp()` (returns `(HttpClient Client, WebApplicationFactory<Program> Factory)`, seed admin `root`/`passwort123`).
- Produces: `NauditDbContext : IDataProtectionKeyContext` with `DbSet<DataProtectionKey> DataProtectionKeys` (entity type from `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore`); the `DataProtectionKeys` table (columns `Id`, `FriendlyName`, `Xml`) on both providers.

- [ ] **Step 1: Add the package**

In `src/Naudit.Infrastructure/Naudit.Infrastructure.csproj`, add next to the other EF packages (after the `Npgsql.EntityFrameworkCore.PostgreSQL` line):

```xml
    <PackageReference Include="Microsoft.AspNetCore.DataProtection.EntityFrameworkCore" Version="10.0.9" />
```

(The `PersistKeysToDbContext` extension for `Program.cs` flows to Web transitively via the project reference.)

- [ ] **Step 2: Write the failing tests**

Add to `tests/Naudit.Tests/NauditDbContextTests.cs` (new using at the top: `using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;`):

```csharp
    [Fact]
    public async Task Migrate_createsDataProtectionKeysTable_andRoundtrips()
    {
        await using var db = CreateDb();
        db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = "key-1", Xml = "<key id=\"1\" />" });
        await db.SaveChangesAsync();
        Assert.Equal("<key id=\"1\" />", (await db.DataProtectionKeys.SingleAsync()).Xml);
    }
```

Add to `tests/Naudit.Tests/AuthEndpointTests.cs` (new usings: `using Microsoft.EntityFrameworkCore;` and `using Microsoft.Extensions.DependencyInjection;`):

```csharp
    [Fact]
    public async Task Login_persistsDataProtectionKeys_inDatabase()
    {
        // Erster Login erzeugt den Key-Ring lazy — der Signatur-Key muss in der DB landen,
        // damit Sessions Container-Neustarts überleben (beide Backends, kein Key-Verzeichnis).
        var (client, factory) = CreateApp();
        var ok = await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Data.NauditDbContext>();
        Assert.True(await db.DataProtectionKeys.AnyAsync());
    }
```

- [ ] **Step 3: Run the new tests to verify they fail**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~DataProtectionKeys"`
Expected: **build error** — `NauditDbContext.DataProtectionKeys` does not exist yet.

- [ ] **Step 4: Implement `IDataProtectionKeyContext`**

In `src/Naudit.Infrastructure/Data/NauditDbContext.cs`, add the using, the interface, and the DbSet:

```csharp
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Naudit.Infrastructure.Data;

public sealed class NauditDbContext(DbContextOptions<NauditDbContext> options)
    : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    public DbSet<GitHubLinkEntity> GitHubLinks => Set<GitHubLinkEntity>();
    public DbSet<ProjectEntity> Projects => Set<ProjectEntity>();
    public DbSet<ReviewEntity> Reviews => Set<ReviewEntity>();
    public DbSet<ReviewFindingEntity> ReviewFindings => Set<ReviewFindingEntity>();

    /// <summary>Data-Protection-Keys (Session-Cookie-Signatur) — in der DB statt im Dateisystem,
    /// damit Sessions Container-Neustarts auf beiden Backends überleben.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    // OnModelCreating unverändert lassen
```

(`OnModelCreating` stays exactly as it is.)

- [ ] **Step 5: Generate the migration**

```bash
dotnet tool install --global dotnet-ef 2>/dev/null || dotnet tool update --global dotnet-ef
dotnet build Naudit.slnx
dotnet ef migrations add AddDataProtectionKeys --project src/Naudit.Infrastructure --output-dir Data/Migrations
```

Expected: three files touched under `src/Naudit.Infrastructure/Data/Migrations/` — new `<timestamp>_AddDataProtectionKeys.cs`, new `<timestamp>_AddDataProtectionKeys.Designer.cs`, updated `NauditDbContextModelSnapshot.cs`. The generated code is SQLite-baked (`type: "INTEGER"` / `"TEXT"`).

- [ ] **Step 6: Neutralize the migration (`Up()`/`Down()`)**

Overwrite the body of `<timestamp>_AddDataProtectionKeys.cs` so the file reads (keep the generated class/file name; timestamps differ):

```csharp
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataProtectionKeys : Migration
    {
        // HINWEIS: Wie InitialUi bewusst PROVIDER-NEUTRAL handgepflegt (keine expliziten
        // Spaltentypen, beide Identity-Strategien annotiert) — Begründung s. 20260707170820_InitialUi.cs.
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DataProtectionKeys",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false)
                        .Annotation("Sqlite:Autoincrement", true)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FriendlyName = table.Column<string>(nullable: true),
                    Xml = table.Column<string>(nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataProtectionKeys", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DataProtectionKeys");
        }
    }
}
```

- [ ] **Step 7: Neutralize the Designer**

Strip every `.HasColumnType(...)` from the new Designer (the chained-call lines carry the `;`, so join-remove them):

```bash
perl -0pi -e 's/\n\s*\.HasColumnType\("[^"]+"\)//g' src/Naudit.Infrastructure/Data/Migrations/*_AddDataProtectionKeys.Designer.cs
```

Then add the convention header directly under the `// <auto-generated />` line of that Designer file:

```csharp
// HINWEIS: Bewusst PROVIDER-NEUTRAL handgepflegt (s. <timestamp>_AddDataProtectionKeys.cs): der
// Migration-SQL-Generator liest bei `type: null` in Up() den Spaltentyp aus DIESEM TargetModel.
// Deshalb sind hier alle .HasColumnType(...) entfernt — jeder Provider wählt seinen Default.
// Bei einem künftigen `dotnet ef migrations add` analog neutralisieren.
```

**Do not touch** `NauditDbContextModelSnapshot.cs` beyond what the tool regenerated — the snapshot stays SQLite-baked by convention (see Global Constraints). Verify the InitialUi files are untouched: `git diff --stat -- src/Naudit.Infrastructure/Data/Migrations/20260707170820_InitialUi.cs src/Naudit.Infrastructure/Data/Migrations/20260707170820_InitialUi.Designer.cs` shows no changes.

- [ ] **Step 8: Persist keys to the DbContext in `Program.cs`**

In `src/Naudit.Web/Program.cs`, re-add `using Microsoft.AspNetCore.DataProtection;` to the usings, and inside the `if (uiConfig.Enabled)` block (directly after `builder.Services.AddAuthorization();`) insert:

```csharp
    // Data-Protection-Keys (Session-Cookie-Signatur) in die DB: überleben Container-Neustarts
    // auf beiden Backends, kein Key-Verzeichnis/Volume nötig. UI ⇒ DB garantiert den DbContext.
    builder.Services.AddDataProtection()
        .PersistKeysToDbContext<Naudit.Infrastructure.Data.NauditDbContext>();
```

- [ ] **Step 9: Run the new tests, then the full suite**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~DataProtectionKeys"`
Expected: PASS (2 tests). A `PendingModelChangesWarning`/`InvalidOperationException` from `Migrate()` in any SQLite test means model and regenerated snapshot diverged — recheck Steps 4-7.

Run: `dotnet test Naudit.slnx`
Expected: PASS.

- [ ] **Step 10: Extend the opt-in Postgres round-trip**

In `tests/Naudit.Tests/NauditDbContextPostgresTests.cs` add `using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;` at the top and extend `Migrate_onPostgres_createsSchema_andRoundtripsAggregate` before the final asserts:

```csharp
        // Neue DataProtectionKeys-Tabelle (Migration AddDataProtectionKeys) muss auf Postgres greifen.
        db.DataProtectionKeys.Add(new DataProtectionKey { FriendlyName = "key-1", Xml = "<key id=\"1\" />" });
        await db.SaveChangesAsync();
        Assert.Equal("<key id=\"1\" />", (await db.DataProtectionKeys.SingleAsync()).Xml);
```

Optional local verification (otherwise the test is a no-op without the env var, and CI stays green):

```bash
docker run -d --name naudit-pg -e POSTGRES_PASSWORD=naudit -e POSTGRES_DB=naudit -p 55432:5432 postgres:17-alpine
NAUDIT_TEST_POSTGRES="Host=localhost;Port=55432;Database=naudit;Username=postgres;Password=naudit" \
  dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter NauditDbContextPostgresTests
docker rm -f naudit-pg
```

Expected: PASS (2 tests).

- [ ] **Step 11: Commit**

```bash
git add -A
git commit -m "feat(db): Data-Protection-Keys in der DB persistieren

NauditDbContext implementiert IDataProtectionKeyContext; neue provider-neutrale
Migration AddDataProtectionKeys (InitialUi unangetastet). Program.cs nutzt
PersistKeysToDbContext statt Datei-Persistenz — Sessions überleben Neustarts
auf SQLite wie Postgres, kein Key-Verzeichnis mehr nötig.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```

---

### Task 4: Docs on the new config shape

**Files:**
- Modify: `docs/webui.md`, `docs/deployment.md`, `docs/configuration.md`, `CLAUDE.md`

**Interfaces:**
- Consumes: the final env keys `Naudit__Db__Enabled` / `Naudit__Db__Provider` / `Naudit__Db__ConnectionString` and the fail-fast/DP-keys behaviour from Tasks 1-3.
- Produces: docs only — no code. Docs are **English**.

- [ ] **Step 1: `docs/webui.md`**

1. Replace the enabling env block (lines 23-25 of the ```bash block under "## Enabling (Coolify / environment)"):

```bash
Naudit__Ui__Enabled=true
Naudit__Db__Enabled=true                                    # the UI requires the database (fails fast otherwise)
Naudit__Db__Provider=Sqlite                                 # Sqlite (default) | Postgres
Naudit__Db__ConnectionString="Data Source=/data/naudit.db"  # SQLite: /data = persistent volume!
```

2. In the "### Postgres instead of SQLite" section, change the intro sentence to: *"For a shared/managed database, set `Naudit:Db:Provider=Postgres` and point `Naudit:Db:ConnectionString` at a Npgsql connection string — no `/data` volume needed then:"* and replace the env block with:

```bash
Naudit__Db__Provider=Postgres
Naudit__Db__ConnectionString="Host=db.example.com;Port=5432;Database=naudit;Username=naudit;Password=<secret>"  # 🔒
```

3. After the "Both backends share **one** schema…" paragraph, add:

```markdown
### Database without the dashboard

The database is its own concern (`Naudit:Db:Enabled`), the UI merely depends on it. With
`Naudit__Db__Enabled=true` and the UI off, the access gate and the review audit log are
active without any UI endpoints — useful for a headless deployment (accounts/links are then
managed directly in the database). The reverse is invalid: enabling the UI without the
database fails fast at startup.

Session-cookie signing keys (ASP.NET Data Protection) are stored **in the database** on
both backends, so sessions survive container restarts — no key directory or extra volume.
```

4. In "## Architecture notes", replace the **Persistence** bullet:

```markdown
- **Persistence** is EF Core (`src/Naudit.Infrastructure/Data/`) on **SQLite (default) or
  Postgres** (`Naudit:Db:Provider`), enabled via `Naudit:Db:Enabled` (independently of the
  UI); the schema is applied via `Database.Migrate()` at startup whenever the DB is on. The
  migrations (`InitialUi`, `AddDataProtectionKeys`) are hand-kept provider-neutral (no
  explicit column types, both identity strategies annotated) so a single migration chain
  runs on either backend; on Postgres EF's pending-changes check is suppressed (the
  committed model snapshot is SQLite-flavoured). Data-Protection keys live in the
  `DataProtectionKeys` table (`PersistKeysToDbContext`). A Postgres round-trip is covered
  by the opt-in `NauditDbContextPostgresTests` (runs only when `NAUDIT_TEST_POSTGRES` is set).
```

Also update the **Core seams** bullet's "both no-ops when the UI is off" to "both no-ops when the DB is off".

- [ ] **Step 2: `docs/deployment.md`**

Replace the WebUI block in the env template (lines 54-70) with:

```bash
# ── Database (required for the WebUI; also usable headless: gate + audit log) ──
# Naudit__Db__Enabled=true
# Naudit__Db__Provider=Sqlite                                 # Sqlite (default) | Postgres
# Naudit__Db__ConnectionString="Data Source=/data/naudit.db"  # SQLite: mount a persistent volume at /data!
# Postgres instead (no /data volume needed):
# Naudit__Db__Provider=Postgres
# Naudit__Db__ConnectionString="Host=db.example.com;Port=5432;Database=naudit;Username=naudit;Password=<secret>"  # 🔒

# ── WebUI (optional — access gate + dashboard, see docs/webui.md) ───
# Naudit__Ui__Enabled=true                       # requires Naudit__Db__Enabled=true
# Naudit__Ui__Admin__Username=admin
# Naudit__Ui__Admin__InitialPassword=<secret>    # 🔒 seed admin (first start, empty DB)
# Naudit__Ui__Auth__GitHub__Enabled=false        # optional self-service sign-in
# Naudit__Ui__Auth__GitHub__ClientId=
# Naudit__Ui__Auth__GitHub__ClientSecret=        # 🔒
# Naudit__Ui__Auth__Oidc__Enabled=false
# Naudit__Ui__Auth__Oidc__Authority=
# Naudit__Ui__Auth__Oidc__ClientId=
# Naudit__Ui__Auth__Oidc__ClientSecret=          # 🔒
```

(The `Naudit__Ui__DataProtectionKeysDir` line is gone — keys are in the DB.) Update the "**WebUI volume:**" note right below to reference `Naudit__Db__Enabled` / `Naudit__Db__Provider=Postgres` instead of the `Ui` keys, and add one sentence: "Session signing keys are stored in the database — no extra key volume."

Add a short migration callout at the end of that section (existing deployments):

```markdown
> **Breaking (pre-release rename):** deployments that used the WebUI preview must rename
> `Naudit__Ui__Db` → `Naudit__Db__ConnectionString`, `Naudit__Ui__DbProvider` →
> `Naudit__Db__Provider`, add `Naudit__Db__Enabled=true`, and drop
> `Naudit__Ui__DataProtectionKeysDir`. Data is untouched (same schema); active WebUI
> sessions are invalidated once (keys move to the database).
```

- [ ] **Step 3: `docs/configuration.md`**

Replace the three rows `Naudit:Ui:DbProvider`, `Naudit:Ui:Db`, `Naudit:Ui:DataProtectionKeysDir` (lines 60-62) with — and update the `Naudit:Ui:Enabled` row above them:

```markdown
| `Naudit:Ui:Enabled` | WebUI (dashboard + BFF auth) — **default `false`**; requires `Naudit:Db:Enabled=true` (fails fast otherwise; see [WebUI](webui.md)) |
| `Naudit:Db:Enabled` | Naudit's database (access gate, review audit log, accounts, session keys) — **default `false`**; works headless without the UI |
| `Naudit:Db:Provider` | `Sqlite` (default) \| `Postgres` — persistence backend (same schema/migrations for both) |
| `Naudit:Db:ConnectionString` | Connection string for the chosen backend: SQLite `Data Source=/data/naudit.db` (default — mount a volume!) or Postgres `Host=…;Database=…;Username=…;Password=…` |
```

- [ ] **Step 4: `CLAUDE.md`**

Replace the extension-point bullet "**WebUI: access gate + dashboard (opt-in):**" with:

```markdown
- **DB (first-class concern) + WebUI on top (both opt-in):** the database is its own config
  section `Naudit:Db` (`Enabled` default `false`, `Provider` = `Sqlite`|`Postgres`,
  `ConnectionString`), decoupled from the UI. `Naudit:Db:Enabled` switches
  `NauditDbContext` + `EfAccessGate` + `EfReviewAuditSink` (off ⇒ `AllowAllAccessGate` +
  `NullReviewAuditSink` = pre-WebUI behaviour); `Naudit:Ui:Enabled` switches
  dashboard/BFF-auth/`AccountService` and **requires the DB — fail-fast at startup
  otherwise** (UI ⇒ DB; gate + audit log work headless without the dashboard). Two Core
  seams in the `IPromptRedactor` pattern: `IAccessGate` (checked in both webhook endpoints
  before enqueue — silent drop with 200 — and in `POST /review` — 403) and
  `IReviewAuditSink` (called after `PostReviewAsync` with verdict/findings/token usage from
  MEAI `ChatResponse.Usage`; failures never fail the review). Implementations in
  `src/Naudit.Infrastructure/Ui/`, persistence in `src/Naudit.Infrastructure/Data/` (EF
  Core, migration via `Database.Migrate()` at startup whenever the DB is on). ASP.NET
  Data-Protection keys are persisted **in the DB** (`NauditDbContext` implements
  `IDataProtectionKeyContext`, `PersistKeysToDbContext` in `Program.cs`) — sessions survive
  restarts on both backends. The migrations (`InitialUi`, `AddDataProtectionKeys`) are
  hand-kept provider-neutral: no explicit column types, both `Sqlite:Autoincrement` and
  `Npgsql:ValueGenerationStrategy` annotated in `Up()`, no `HasColumnType` in the
  Designers; on Postgres the `PendingModelChangesWarning` is suppressed. A future `dotnet
  ef migrations add` re-bakes SQLite types — re-neutralize the new migration + Designer
  (snapshot stays SQLite-baked). Postgres round-trip: opt-in `NauditDbContextPostgresTests`,
  gated on `NAUDIT_TEST_POSTGRES`. BFF-auth + JSON API in `src/Naudit.Web/Endpoints/`
  (cookie session, 401 instead of redirects); the React SPA in `src/frontend/` is compiled
  into `wwwroot/` by the container build. The Settings API/page is **read-only** by design
  (config stays env-only). See `docs/webui.md`.
```

Also in the CLAUDE.md architecture intro of the `Naudit.Infrastructure` bullet, "persistence in `src/Naudit.Infrastructure/Data/` (EF Core on **SQLite (default) or Postgres** via `Naudit:Ui:DbProvider` …" appears only in the extension-point bullet replaced above — verify with `grep -n "Naudit:Ui:Db" CLAUDE.md` that nothing is left.

- [ ] **Step 5: Final leftover scan + suite**

Run: `grep -rn "Naudit:Ui:Db\|Naudit__Ui__Db\|DataProtectionKeysDir\|UiDbProvider" src tests docs README.md CLAUDE.md --include='*' | grep -v superpowers`
Expected: no matches (the spec/plan under `docs/superpowers/` keep the historical names).

Run: `dotnet test Naudit.slnx`
Expected: PASS (docs-only task, safety net).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "docs(db): Doku auf die Naudit:Db-Sektion umstellen

webui/deployment/configuration/CLAUDE.md: neue Env-Namen (Naudit__Db__*),
DataProtectionKeysDir entfällt (Keys in der DB), Headless-DB dokumentiert,
Breaking-Note für bestehende Preview-Deployments.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>"
```
