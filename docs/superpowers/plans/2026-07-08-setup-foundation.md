# Setup-Fundament (PR 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Config lebt in der DB (env als Override), DB + UI immer an, Host-Neustart-Schleife mit Recovery-Modus, `AccessGate:Mode`, editierbare Settings-Seite — das Fundament aus `docs/superpowers/specs/2026-07-08-setup-wizard-design.md` (PR 1 von 3).

**Architecture:** Ein Bootstrap-Loader (`DbSettingsLoader`) migriert die DB und lädt die `Settings`-Tabelle **vor** dem Host-Bau; die Werte werden als Memory-Quelle **unter** User-Secrets/Env in die .NET-Config-Pipeline eingefügt (`appsettings < DB < user-secrets/env`). `AddNauditInfrastructure` und alle Options bleiben unverändert. `Program.cs` wird eine `while`-Schleife: `IAppRestarter` stoppt den Host, die Schleife baut ihn neu (Config-Änderung übernehmen ≈ 1–2 s). Wirft die Infrastruktur-Registrierung eine Config-Exception, wird per **Probe** erkannt und die App startet im Recovery-Modus (UI + Settings-API an, Webhooks/Review aus) statt in einer Crash-Loop.

**Tech Stack:** .NET 10 Minimal API, EF Core (SQLite/Postgres, provider-neutrale Migrationen), ASP.NET Data Protection (Keys in DB), xUnit + `WebApplicationFactory`, React/Vite/Tailwind 4 + TanStack Query.

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — `dotnet test Naudit.slnx`, nie `Naudit.sln`.
- Code-Kommentare auf Deutsch; `docs/**` und README auf Englisch.
- Dependency-Regel: `Web → Infrastructure → Core`; Core kennt nur MEAI-Abstractions. (Dieser Plan fasst Core nicht an.)
- Migrationen provider-neutral handgepflegt: keine expliziten Spaltentypen in `Up()`, kein `HasColumnType` im neuen Designer; der Model-Snapshot bleibt SQLite-geprägt (CLAUDE.md-Prozedur).
- Frontend-Gate: `cd src/frontend && npm run lint && npm run build` muss grün sein.
- Commits klein und pro Task; Präfixe wie im Repo üblich (`feat:`, `test:`, `docs:`, `refactor:`).
- Data-Protection-Purpose für Settings-Secrets: exakt `"Naudit.Settings"`; DP-`SetApplicationName` exakt `"Naudit"` (Loader und Host müssen kompatibel verschlüsseln).

---

### Task 1: Settings- & SetupDraft-Entities + provider-neutrale Migration

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs`
- Modify: `src/Naudit.Infrastructure/Data/NauditDbContext.cs`
- Create: `src/Naudit.Infrastructure/Data/Migrations/<timestamp>_AddSettingsAndSetupDraft.cs` (+ Designer, via `dotnet ef`, dann neutralisieren)
- Test: `tests/Naudit.Tests/NauditDbContextTests.cs` (ergänzen)

**Interfaces:**
- Produces: `SettingEntity { string Key; string Value; bool IsSecret; DateTime UpdatedAtUtc }`, `SetupDraftEntity { int Id; string Json; DateTime UpdatedAtUtc }`, `NauditDbContext.Settings`, `NauditDbContext.SetupDrafts`.

- [ ] **Step 1: Failing Test schreiben** — in `tests/Naudit.Tests/NauditDbContextTests.cs` (bestehende Konventionen der Datei übernehmen, z. B. wie dort ein SQLite-Kontext aufgebaut wird):

```csharp
[Fact]
public async Task Settings_roundtrip_persistiertKeyValueSecretFlag()
{
    await using var db = CreateContext(); // vorhandenen Helper der Testklasse nutzen
    db.Settings.Add(new SettingEntity
    {
        Key = "Naudit:Ai:Provider",
        Value = "Anthropic",
        IsSecret = false,
        UpdatedAtUtc = DateTime.UtcNow,
    });
    db.SetupDrafts.Add(new SetupDraftEntity { Id = 1, Json = "{}", UpdatedAtUtc = DateTime.UtcNow });
    await db.SaveChangesAsync();

    var setting = await db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:Provider");
    Assert.Equal("Anthropic", setting.Value);
    Assert.False(setting.IsSecret);
    Assert.Equal("{}", (await db.SetupDrafts.SingleAsync(d => d.Id == 1)).Json);
}
```

Hinweis: Falls die Testklasse keinen `CreateContext`-Helper hat, den dortigen Aufbau (Options + `Database.Migrate()`/`EnsureCreated`) einer bestehenden Testmethode spiegeln.

- [ ] **Step 2: Test läuft rot**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~Settings_roundtrip"`
Expected: Compile-Fehler (`SettingEntity` unbekannt) — das zählt als rot.

- [ ] **Step 3: Entities + DbContext implementieren** — ans Ende von `Entities.cs`:

```csharp
/// <summary>Ein verwalteter Konfigurationswert (Key in Doppelpunkt-Notation, z. B. "Naudit:Ai:Provider").
/// Secrets liegen Data-Protection-verschlüsselt in Value (Purpose "Naudit.Settings").</summary>
public sealed class SettingEntity
{
    public required string Key { get; set; }
    public required string Value { get; set; }
    public bool IsSecret { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>Zwischenstand des Setup-Wizards (genau eine Zeile, Id=1) — JSON-Blob,
/// DP-verschlüsselt. Wird erst beim "Übernehmen" in echte Settings umgesetzt.</summary>
public sealed class SetupDraftEntity
{
    public int Id { get; set; }
    public required string Json { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
```

In `NauditDbContext.cs` DbSets ergänzen (nach `ReviewFindings`):

```csharp
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();
    public DbSet<SetupDraftEntity> SetupDrafts => Set<SetupDraftEntity>();
```

und in `OnModelCreating` (nach dem `ReviewFindingEntity`-Block):

```csharp
        b.Entity<SettingEntity>(e => e.HasKey(x => x.Key));
        // Id wird von der App gesetzt (immer 1) — kein Autoincrement, hält die Migration provider-neutral.
        b.Entity<SetupDraftEntity>(e => e.Property(x => x.Id).ValueGeneratedNever());
```

- [ ] **Step 4: Migration erzeugen und neutralisieren**

```bash
dotnet tool restore 2>/dev/null || dotnet tool install --global dotnet-ef
dotnet ef migrations add AddSettingsAndSetupDraft --project src/Naudit.Infrastructure
```

Dann die neue `<timestamp>_AddSettingsAndSetupDraft.cs` öffnen und **alle `type:`-Argumente aus den `table.Column<...>(...)`-Aufrufen entfernen** (SQLite-Typen wie `"TEXT"`/`"INTEGER"`). Ziel-`Up()`:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(nullable: false),
                    Value = table.Column<string>(nullable: false),
                    IsSecret = table.Column<bool>(nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_Settings", x => x.Key));

            migrationBuilder.CreateTable(
                name: "SetupDrafts",
                columns: table => new
                {
                    Id = table.Column<int>(nullable: false),
                    Json = table.Column<string>(nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(nullable: false),
                },
                constraints: table => table.PrimaryKey("PK_SetupDrafts", x => x.Id));
        }
```

Im zugehörigen `.Designer.cs` alle `HasColumnType("...")`-Aufrufe der **neuen** Entities entfernen. Der `NauditDbContextModelSnapshot.cs` bleibt wie von `dotnet ef` generiert (SQLite-geprägt — bewusst, siehe CLAUDE.md). Keine Autoincrement-Annotationen nötig (String-PK bzw. `ValueGeneratedNever`).

- [ ] **Step 5: Test läuft grün + volle Suite**

Run: `dotnet test Naudit.slnx`
Expected: alle Tests PASS (249 + 1 neuer).

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Infrastructure/Data tests/Naudit.Tests/NauditDbContextTests.cs
git commit -m "feat(db): Settings- und SetupDraft-Tabellen (provider-neutrale Migration)"
```

---

### Task 2: SettingsCatalog + SettingsService (Verschlüsselung, Whitelist)

**Files:**
- Create: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Create: `src/Naudit.Infrastructure/Settings/SettingsService.cs`
- Test: `tests/Naudit.Tests/SettingsServiceTests.cs`

**Interfaces:**
- Consumes: `NauditDbContext.Settings` (Task 1).
- Produces:
  - `sealed record SettingDefinition(string Key, bool IsSecret)`
  - `static class SettingsCatalog { public static IReadOnlyList<SettingDefinition> All { get; } public static bool TryGet(string key, out SettingDefinition definition) }` (Key-Vergleich case-insensitive)
  - `sealed class SettingsService(NauditDbContext db, IDataProtectionProvider dataProtection)`:
    - `public const string ProtectorPurpose = "Naudit.Settings";`
    - `Task SetAsync(string key, string value, CancellationToken ct = default)` — wirft `InvalidOperationException` bei Key außerhalb des Katalogs; verschlüsselt bei `IsSecret`
    - `Task<bool> RemoveAsync(string key, CancellationToken ct = default)`
    - `Task<HashSet<string>> GetSetKeysAsync(CancellationToken ct = default)` (case-insensitive Set der in der DB vorhandenen Keys)

- [ ] **Step 1: Failing Tests schreiben** — `tests/Naudit.Tests/SettingsServiceTests.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>SettingsService: Katalog-Whitelist, Secret-Verschlüsselung, Upsert/Remove.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly NauditDbContext _db;
    private readonly SettingsService _service;

    public SettingsServiceTests()
    {
        _conn.Open();
        _db = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        _db.Database.EnsureCreated();
        _service = new SettingsService(_db, new EphemeralDataProtectionProvider());
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    [Fact]
    public async Task SetAsync_plainKey_speichertKlartextUndUpserted()
    {
        await _service.SetAsync("Naudit:Ai:Provider", "Anthropic");
        await _service.SetAsync("Naudit:Ai:Provider", "Ollama"); // Upsert, kein Duplikat
        var row = await _db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:Provider");
        Assert.Equal("Ollama", row.Value);
        Assert.False(row.IsSecret);
    }

    [Fact]
    public async Task SetAsync_secretKey_speichertNieKlartext()
    {
        await _service.SetAsync("Naudit:Ai:ApiKey", "sk-super-geheim");
        var row = await _db.Settings.SingleAsync(s => s.Key == "Naudit:Ai:ApiKey");
        Assert.True(row.IsSecret);
        Assert.DoesNotContain("sk-super-geheim", row.Value);
    }

    [Fact]
    public async Task SetAsync_unbekannterKey_wirft()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SetAsync("Naudit:Nope", "x"));
    }

    [Fact]
    public async Task RemoveAsync_undGetSetKeys()
    {
        await _service.SetAsync("Naudit:Ai:Model", "sonnet");
        Assert.Contains("Naudit:Ai:Model", await _service.GetSetKeysAsync());
        Assert.True(await _service.RemoveAsync("Naudit:Ai:Model"));
        Assert.False(await _service.RemoveAsync("Naudit:Ai:Model")); // schon weg
        Assert.Empty(await _service.GetSetKeysAsync());
    }

    [Fact]
    public void Catalog_kenntKernKeys_mitKorrektemSecretFlag()
    {
        Assert.True(SettingsCatalog.TryGet("Naudit:Ai:ApiKey", out var apiKey));
        Assert.True(apiKey.IsSecret);
        Assert.True(SettingsCatalog.TryGet("naudit:git:platform", out var platform)); // case-insensitive
        Assert.False(platform.IsSecret);
        Assert.True(SettingsCatalog.TryGet("Naudit:GitHub:App:PrivateKey", out var pem));
        Assert.True(pem.IsSecret);
        Assert.True(SettingsCatalog.TryGet("Naudit:AccessGate:Mode", out _));
        Assert.False(SettingsCatalog.TryGet("Naudit:Db:ConnectionString", out _)); // Bootstrap-Key: nie DB-verwaltet
    }
}
```

- [ ] **Step 2: Test läuft rot**

Run: `dotnet test Naudit.slnx --filter SettingsServiceTests`
Expected: Compile-Fehler (Namespace `Naudit.Infrastructure.Settings` fehlt).

- [ ] **Step 3: Implementieren** — `SettingsCatalog.cs`:

```csharp
namespace Naudit.Infrastructure.Settings;

/// <summary>Ein DB-verwaltbarer Konfigurationswert. IsSecret steuert Verschlüsselung
/// und Write-only-Verhalten der Settings-API.</summary>
public sealed record SettingDefinition(string Key, bool IsSecret);

/// <summary>Whitelist der DB-verwaltbaren Keys. Bootstrap-Keys (Naudit:Db:*, ForwardedHeaders,
/// Ports) fehlen hier bewusst — sie müssen vor dem DB-Zugriff bekannt sein und bleiben env-only.
/// Listen-Keys (ProjectTokens, Ui:Admins) bleiben vorerst ebenfalls env-only (Index-Keys passen
/// schlecht in ein Key/Value-Formular).</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<SettingDefinition> All { get; } =
    [
        new("Naudit:PublicBaseUrl", false),
        new("Naudit:Git:Platform", false),
        new("Naudit:GitLab:BaseUrl", false),
        new("Naudit:GitLab:Token", true),
        new("Naudit:GitLab:WebhookSecret", true),
        new("Naudit:GitLab:PostVerdict", false),
        new("Naudit:GitHub:BaseUrl", false),
        new("Naudit:GitHub:Token", true),
        new("Naudit:GitHub:WebhookSecret", true),
        new("Naudit:GitHub:Auth", false),
        new("Naudit:GitHub:App:AppId", false),
        new("Naudit:GitHub:App:PrivateKey", true),
        new("Naudit:GitHub:App:InstallationId", false),
        new("Naudit:GitHub:PostVerdict", false),
        new("Naudit:Ai:Provider", false),
        new("Naudit:Ai:Model", false),
        new("Naudit:Ai:Endpoint", false),
        new("Naudit:Ai:ApiKey", true),
        new("Naudit:Review:SystemPrompt", false),
        new("Naudit:Review:Gate:MinSeverity", false),
        new("Naudit:Review:Gate:MinConfidence", false),
        new("Naudit:AccessGate:Mode", false),
        new("Naudit:Ui:Auth:GitHub:Enabled", false),
        new("Naudit:Ui:Auth:GitHub:ClientId", false),
        new("Naudit:Ui:Auth:GitHub:ClientSecret", true),
        new("Naudit:Ui:Auth:Oidc:Enabled", false),
        new("Naudit:Ui:Auth:Oidc:Authority", false),
        new("Naudit:Ui:Auth:Oidc:ClientId", false),
        new("Naudit:Ui:Auth:Oidc:ClientSecret", true),
    ];

    private static readonly Dictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string key, out SettingDefinition definition) =>
        ByKey.TryGetValue(key, out definition!);
}
```

`SettingsService.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Settings;

/// <summary>Schreibt/löscht DB-verwaltete Settings. Secrets werden mit Data Protection
/// verschlüsselt (Purpose unten) — der DbSettingsLoader entschlüsselt sie beim Bootstrap.</summary>
public sealed class SettingsService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.Settings";

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        if (!SettingsCatalog.TryGet(key, out var def))
            throw new InvalidOperationException($"'{key}' ist kein verwalteter Setting-Key.");

        var stored = def.IsSecret
            ? dataProtection.CreateProtector(ProtectorPurpose).Protect(value)
            : value;

        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == def.Key, ct);
        if (row is null)
            db.Settings.Add(new SettingEntity { Key = def.Key, Value = stored, IsSecret = def.IsSecret, UpdatedAtUtc = DateTime.UtcNow });
        else
        {
            row.Value = stored;
            row.IsSecret = def.IsSecret;
            row.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken ct = default)
    {
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, ct);
        if (row is null) return false;
        db.Settings.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<HashSet<string>> GetSetKeysAsync(CancellationToken ct = default) =>
        new(await db.Settings.Select(s => s.Key).ToListAsync(ct), StringComparer.OrdinalIgnoreCase);
}
```

- [ ] **Step 4: Tests grün**

Run: `dotnet test Naudit.slnx --filter SettingsServiceTests`
Expected: 5 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Settings tests/Naudit.Tests/SettingsServiceTests.cs
git commit -m "feat(settings): SettingsCatalog (Whitelist) + SettingsService mit DP-Verschluesselung"
```

---

### Task 3: DbSettingsLoader (Bootstrap: migrieren, laden, entschlüsseln)

**Files:**
- Create: `src/Naudit.Infrastructure/Settings/DbSettingsLoader.cs`
- Modify: `src/Naudit.Infrastructure/Data/DatabaseOptions.cs` (statischen `ConfigureDbContext`-Helper ergänzen — noch OHNE die Enabled-Entfernung, die kommt in Task 6)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Provider-Weiche auf den Helper umstellen)
- Test: `tests/Naudit.Tests/DbSettingsLoaderTests.cs`

**Interfaces:**
- Consumes: `SettingsService.ProtectorPurpose`, `NauditDbContext` (Tasks 1–2).
- Produces:
  - `DatabaseOptions.ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseOptions options)` — statisch; die eine Provider-Weiche (SQLite/Postgres inkl. `PendingModelChangesWarning`-Suppression) für DI **und** Loader
  - `static class DbSettingsLoader { public static DbSettingsLoadResult Load(DatabaseOptions options) }`
  - `sealed record DbSettingsLoadResult(Dictionary<string, string?> Settings, List<string> Warnings)`
  - DP-App-Name-Konstante: `DbSettingsLoader.DataProtectionAppName = "Naudit"`

- [ ] **Step 1: Failing Tests** — `tests/Naudit.Tests/DbSettingsLoaderTests.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Bootstrap-Loader: migriert, liest Settings, entschlüsselt Secrets über einen
/// EIGENEN DP-Provider (gleiche DB, gleicher AppName) — nicht entschlüsselbar ⇒ Warnung statt Crash.</summary>
public sealed class DbSettingsLoaderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"naudit-loader-{Guid.NewGuid():N}", "naudit.db");
    private DatabaseOptions Options => new() { ConnectionString = $"Data Source={_dbPath}" };

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_dbPath)!;
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void Load_frischeDb_legtVerzeichnisAnUndLiefertLeer()
    {
        var result = DbSettingsLoader.Load(Options); // Verzeichnis existiert noch nicht
        Assert.Empty(result.Settings);
        Assert.Empty(result.Warnings);
        Assert.True(File.Exists(_dbPath)); // migriert & angelegt
    }

    [Fact]
    public void Load_liestKlartextUndEntschluesseltSecrets()
    {
        DbSettingsLoader.Load(Options); // DB anlegen/migrieren
        // Schreiben wie die App es tut: DP-Keyring in DERSELBEN DB (PersistKeysToDbContext).
        WriteViaService(svc =>
        {
            svc.SetAsync("Naudit:Ai:Provider", "Anthropic").GetAwaiter().GetResult();
            svc.SetAsync("Naudit:Ai:ApiKey", "sk-geheim").GetAwaiter().GetResult();
        });

        var result = DbSettingsLoader.Load(Options); // eigener, frischer DP-Provider
        Assert.Equal("Anthropic", result.Settings["Naudit:Ai:Provider"]);
        Assert.Equal("sk-geheim", result.Settings["Naudit:Ai:ApiKey"]);
    }

    [Fact]
    public void Load_kaputtesSecret_wirdUebersprungenMitWarnung()
    {
        DbSettingsLoader.Load(Options);
        using (var db = OpenContext())
        {
            db.Settings.Add(new SettingEntity
            {
                Key = "Naudit:Ai:ApiKey", Value = "kein-gueltiger-ciphertext",
                IsSecret = true, UpdatedAtUtc = DateTime.UtcNow,
            });
            db.SaveChanges();
        }
        var result = DbSettingsLoader.Load(Options);
        Assert.False(result.Settings.ContainsKey("Naudit:Ai:ApiKey"));
        Assert.Contains(result.Warnings, w => w.Contains("Naudit:Ai:ApiKey"));
    }

    private NauditDbContext OpenContext()
    {
        var b = new DbContextOptionsBuilder<NauditDbContext>();
        DatabaseOptions.ConfigureDbContext(b, Options);
        return new NauditDbContext(b.Options);
    }

    /// <summary>Baut denselben Minimal-Stack wie der Loader (DbContext + DP-Keys in der DB),
    /// um Settings so zu schreiben, wie die laufende App es täte.</summary>
    private void WriteViaService(Action<SettingsService> write)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NauditDbContext>(b => DatabaseOptions.ConfigureDbContext(b, Options));
        services.AddDataProtection().PersistKeysToDbContext<NauditDbContext>()
            .SetApplicationName(DbSettingsLoader.DataProtectionAppName);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        write(new SettingsService(
            scope.ServiceProvider.GetRequiredService<NauditDbContext>(),
            scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()));
    }
}
```

- [ ] **Step 2: Rot** — `dotnet test Naudit.slnx --filter DbSettingsLoaderTests` ⇒ Compile-Fehler.

- [ ] **Step 3: Implementieren.** Zuerst der geteilte Helper in `DatabaseOptions.cs` (Klasse ergänzen, `Enabled` bleibt in diesem Task unangetastet):

```csharp
    /// <summary>Die EINE Provider-Weiche für DbContext-Konfiguration — von DI und
    /// DbSettingsLoader gemeinsam genutzt, damit Bootstrap und Laufzeit nie divergieren.</summary>
    public static void ConfigureDbContext(DbContextOptionsBuilder builder, DatabaseOptions options)
    {
        switch (options.Provider)
        {
            case DbProvider.Postgres:
                builder.UseNpgsql(options.ConnectionString);
                // Snapshot ist SQLite-geprägt — konventionsbedingter Diff auf Postgres ist gutartig.
                builder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
                break;
            default:
                builder.UseSqlite(options.ConnectionString);
                break;
        }
    }
```

(Usings in `DatabaseOptions.cs` ergänzen: `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.Diagnostics`.)
In `DependencyInjection.cs` den `switch (dbOptions.Provider)`-Block im `AddDbContext`-Lambda durch `DatabaseOptions.ConfigureDbContext(o, dbOptions);` ersetzen.

Dann `DbSettingsLoader.cs`:

```csharp
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Settings;

/// <summary>Bootstrap vor dem Host-Bau: SQLite-Verzeichnis anlegen, DB migrieren, Settings
/// lesen und Secrets entschlüsseln. Baut dafür einen eigenen Minimal-ServiceProvider
/// (DbContext + DP mit Keyring in derselben DB) — der Host existiert hier noch nicht.</summary>
public static class DbSettingsLoader
{
    /// <summary>Fester DP-Anwendungsname: Loader und Host müssen denselben verwenden,
    /// sonst sind gegenseitig verschlüsselte Werte nicht lesbar.</summary>
    public const string DataProtectionAppName = "Naudit";

    public static DbSettingsLoadResult Load(DatabaseOptions options)
    {
        EnsureSqliteDirectory(options);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<NauditDbContext>(b => DatabaseOptions.ConfigureDbContext(b, options));
        services.AddDataProtection().PersistKeysToDbContext<NauditDbContext>()
            .SetApplicationName(DataProtectionAppName);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NauditDbContext>();
        db.Database.Migrate();

        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(SettingsService.ProtectorPurpose);

        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        foreach (var row in db.Settings.AsNoTracking().ToList())
        {
            if (!row.IsSecret) { settings[row.Key] = row.Value; continue; }
            try { settings[row.Key] = protector.Unprotect(row.Value); }
            catch (CryptographicException)
            {
                // Keyring weg/DB kopiert: Wert gilt als fehlend, wird neu abgefragt — kein Crash.
                warnings.Add($"Setting '{row.Key}' ist nicht entschlüsselbar und wird ignoriert (Data-Protection-Keyring gewechselt?). Bitte neu setzen.");
            }
        }
        return new DbSettingsLoadResult(settings, warnings);
    }

    /// <summary>SQLite legt Dateien an, aber keine Verzeichnisse — "Data Source=data/naudit.db"
    /// braucht das Verzeichnis vorab. Für Postgres ein No-op.</summary>
    private static void EnsureSqliteDirectory(DatabaseOptions options)
    {
        if (options.Provider != DbProvider.Sqlite) return;
        var dataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(options.ConnectionString).DataSource;
        var dir = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }
}

public sealed record DbSettingsLoadResult(Dictionary<string, string?> Settings, List<string> Warnings);
```

- [ ] **Step 4: Grün + volle Suite** — `dotnet test Naudit.slnx` ⇒ PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure tests/Naudit.Tests/DbSettingsLoaderTests.cs
git commit -m "feat(settings): DbSettingsLoader — Bootstrap-Migration + Settings-Load mit DP-Entschluesselung"
```

---

### Task 4: Config-Pipeline — DB-Quelle einfügen + EnvOverrides

**Files:**
- Create: `src/Naudit.Infrastructure/Settings/NauditConfig.cs`
- Test: `tests/Naudit.Tests/NauditConfigTests.cs`

**Interfaces:**
- Produces:
  - `sealed record EnvOverrides(IConfiguration Root)` — alles, was ÜBER der DB-Quelle liegt (User-Secrets, Env-Vars, CommandLine); `Root[key] != null` ⇒ Key ist env-gesperrt
  - `static class NauditConfig { public static EnvOverrides InsertDbSettings(IConfigurationBuilder configuration, IDictionary<string, string?> dbSettings) }`

- [ ] **Step 1: Failing Tests** — `tests/Naudit.Tests/NauditConfigTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Naudit.Infrastructure.Settings;
using Xunit;

namespace Naudit.Tests;

/// <summary>Precedence-Vertrag: appsettings.json &lt; DB-Settings &lt; User-Secrets/Env.
/// EnvOverrides enthält genau die Quellen oberhalb der DB (fürs "via environment"-Lock der UI).</summary>
public sealed class NauditConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("naudit-config").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteJson(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void DbUeberschreibtAppsettings_EnvUeberschreibtDb()
    {
        var builder = new ConfigurationBuilder();
        builder.AddJsonFile(WriteJson("appsettings.json",
            """{ "Naudit": { "Ai": { "Provider": "Ollama", "Model": "llama3.1" }, "Git": { "Platform": "GitLab" } } }"""));
        builder.AddInMemoryCollection(new Dictionary<string, string?>   // simuliert Env-Vars (liegt NACH appsettings)
        {
            ["Naudit:Git:Platform"] = "GitHub",
        });

        var env = NauditConfig.InsertDbSettings(builder, new Dictionary<string, string?>
        {
            ["Naudit:Ai:Provider"] = "Anthropic",   // DB schlägt appsettings
            ["Naudit:Git:Platform"] = "GitLab",     // Env schlägt DB
        });
        var config = builder.Build();

        Assert.Equal("Anthropic", config["Naudit:Ai:Provider"]); // DB gewinnt über appsettings
        Assert.Equal("llama3.1", config["Naudit:Ai:Model"]);     // appsettings bleibt sichtbar
        Assert.Equal("GitHub", config["Naudit:Git:Platform"]);   // Env gewinnt über DB

        Assert.NotNull(env.Root["Naudit:Git:Platform"]);  // env-gesperrt
        Assert.Null(env.Root["Naudit:Ai:Provider"]);      // nur DB ⇒ nicht gesperrt
        Assert.Null(env.Root["Naudit:Ai:Model"]);         // nur appsettings ⇒ nicht gesperrt
    }

    [Fact]
    public void OhneAppsettingsQuellen_landetDbGanzUnten()
    {
        var builder = new ConfigurationBuilder();
        builder.AddInMemoryCollection(new Dictionary<string, string?> { ["A"] = "env" });
        var env = NauditConfig.InsertDbSettings(builder, new Dictionary<string, string?> { ["A"] = "db", ["B"] = "db" });
        var config = builder.Build();
        Assert.Equal("env", config["A"]);
        Assert.Equal("db", config["B"]);
        Assert.NotNull(env.Root["A"]);
        Assert.Null(env.Root["B"]);
    }
}
```

- [ ] **Step 2: Rot** — `dotnet test Naudit.slnx --filter NauditConfigTests` ⇒ Compile-Fehler.

- [ ] **Step 3: Implementieren** — `NauditConfig.cs`:

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace Naudit.Infrastructure.Settings;

/// <summary>Konfigurationsquellen OBERHALB der DB-Settings (User-Secrets, Env, CommandLine).
/// Root[key] != null ⇒ der Key ist per Umgebung übersteuert und in der UI gesperrt.</summary>
public sealed record EnvOverrides(IConfiguration Root);

public static class NauditConfig
{
    /// <summary>Fügt die DB-Settings als Memory-Quelle DIREKT NACH den appsettings-JSONs ein —
    /// Ergebnis: appsettings &lt; DB &lt; User-Secrets/Env/CommandLine. Liefert die darüberliegenden
    /// Quellen als eigenen Config-Root zurück (für die "via environment"-Erkennung der Settings-API).</summary>
    public static EnvOverrides InsertDbSettings(IConfigurationBuilder configuration, IDictionary<string, string?> dbSettings)
    {
        // Einfügeposition: hinter der LETZTEN appsettings*-JSON-Quelle. User-Secrets sind zwar auch
        // eine JsonConfigurationSource, aber ihr Pfad heißt "secrets.json" — sie bleiben oberhalb.
        var insertAt = 0;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource json &&
                Path.GetFileName(json.Path ?? "").StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
            {
                insertAt = i + 1;
            }
        }
        configuration.Sources.Insert(insertAt, new MemoryConfigurationSource
        {
            InitialData = new Dictionary<string, string?>(dbSettings),
        });

        var overrides = new ConfigurationBuilder();
        foreach (var source in configuration.Sources.Skip(insertAt + 1))
            overrides.Add(source);
        return new EnvOverrides(overrides.Build());
    }
}
```

- [ ] **Step 4: Grün** — `dotnet test Naudit.slnx --filter NauditConfigTests` ⇒ 2 PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Settings/NauditConfig.cs tests/Naudit.Tests/NauditConfigTests.cs
git commit -m "feat(settings): DB-Settings als Config-Quelle unter Env/User-Secrets + EnvOverrides"
```

---

### Task 5: TestAppFactory — jede WAF-Testklasse bekommt ihre eigene Temp-DB

Vorbereitung für „DB immer an": sobald Task 6 landet, migriert **jeder** `WebApplicationFactory`-Host eine DB. Ohne eigene Connection-Strings teilten sich parallele Testklassen die Default-SQLite-Datei (`data/naudit.db`) — Lock-Flakiness. Die Factory ist mit dem heutigen Code harmlos (setzt nur einen ConnectionString, den `Db:Enabled=false` ignoriert), darf also VOR Task 6 landen.

**Files:**
- Create: `tests/Naudit.Tests/Fakes/TestAppFactory.cs`
- Modify: alle 9 Klassen mit `IClassFixture<WebApplicationFactory<Program>>`: `AdminEndpointTests.cs`, `ExternalAuthTests.cs`, `AccessGateEndpointTests.cs`, `AuthEndpointTests.cs`, `DataEndpointTests.cs`, `DbWiringTests.cs`, `ReviewEndpointTests.cs`, `SpaHostingTests.cs`, `WebhookEndpointTests.cs`

**Interfaces:**
- Produces: `sealed class TestAppFactory : WebApplicationFactory<Program>` — setzt pro Instanz einen eindeutigen SQLite-Temp-Pfad als `Naudit:Db:ConnectionString` und räumt ihn beim Dispose weg.

- [ ] **Step 1: Factory implementieren** — `tests/Naudit.Tests/Fakes/TestAppFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Naudit.Tests.Fakes;

/// <summary>WebApplicationFactory mit eigener SQLite-Temp-DB pro Factory-Instanz.
/// Nötig, weil die DB immer an ist: ohne das teilten sich parallel laufende
/// Testklassen die Default-DB-Datei (Lock-Flakiness).</summary>
public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbDir = Directory.CreateTempSubdirectory("naudit-test-db").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Naudit:Db:ConnectionString", $"Data Source={Path.Combine(_dbDir, "naudit.db")}");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dbDir, recursive: true); } catch (IOException) { /* Windows-File-Locks: Temp bleibt */ }
    }
}
```

- [ ] **Step 2: Fixture-Umstellung in allen 9 Dateien.** Mechanisch, je Datei:
  - `IClassFixture<WebApplicationFactory<Program>>` → `IClassFixture<TestAppFactory>`
  - Konstruktor-Parameter und Feldtyp `WebApplicationFactory<Program>` → `TestAppFactory`
  - `using Naudit.Tests.Fakes;` ergänzen, falls nicht da.

  Prüfen mit: `grep -rn "WebApplicationFactory<Program>" tests/` — außer in `Fakes/TestAppFactory.cs` darf nichts mehr auftauchen (`WithWebHostBuilder`-Aufrufe bleiben unverändert, die laufen über die Factory).

- [ ] **Step 3: Volle Suite grün**

Run: `dotnet test Naudit.slnx`
Expected: PASS wie zuvor (Verhalten unverändert — der ConnectionString ist bei `Db:Enabled=false` noch inert; Klassen, die bisher ihre eigene Temp-DB per `UseSetting` setzen, überschreiben die Factory einfach).

- [ ] **Step 4: Commit**

```bash
git add tests/Naudit.Tests
git commit -m "test: TestAppFactory mit eigener Temp-SQLite pro Testklasse (Vorbereitung DB-Pflicht)"
```

---

### Task 6: DB & UI immer an — Flags raus, AccessGate:Mode rein

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/DatabaseOptions.cs` (`Enabled` raus, neuer Default-Pfad)
- Modify: `src/Naudit.Infrastructure/Ui/UiOptions.cs` (`Enabled` raus)
- Create: `src/Naudit.Infrastructure/Ui/AccessGateOptions.cs`
- Modify: `src/Naudit.Infrastructure/Ui/AllowAllAccessGate.cs` (nur XML-Doc)
- Delete: `src/Naudit.Infrastructure/Ui/NullReviewAuditSink.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Modify: `src/Naudit.Web/Program.cs` (Enabled-Verzweigungen raus; Struktur/Schleife kommt erst in Task 7)
- Test: `tests/Naudit.Tests/DbWiringTests.cs` (umschreiben), `DatabaseOptionsTests.cs`, `UiOptionsTests.cs`, `ReviewAuditSinkTests.cs`, `AccessGateEndpointTests.cs`, `SpaHostingTests.cs` (anpassen)

**Interfaces:**
- Produces:
  - `DatabaseOptions { DbProvider Provider; string ConnectionString = "Data Source=data/naudit.db"; }` (kein `Enabled` mehr)
  - `UiOptions` ohne `Enabled` (Rest unverändert)
  - `enum AccessGateMode { Open, Registered }`, `sealed class AccessGateOptions { AccessGateMode Mode { get; set; } = AccessGateMode.Open; }` — Section `Naudit:AccessGate`
  - `DependencyInjection.AddNauditDatabase(this IServiceCollection, IConfiguration)` — registriert `DatabaseOptions`, `NauditDbContext`, `SettingsService`, `AccountService`; von `Program.cs` **vor** `AddNauditInfrastructure` aufgerufen (auch im Recovery-Pfad von Task 7)
  - `AddNauditInfrastructure` registriert DbContext **nicht** mehr selbst; Gate je `AccessGate:Mode`, Audit-Sink immer `EfReviewAuditSink`

- [ ] **Step 1: DbWiringTests umschreiben (rot)** — Datei-Inhalt ersetzen:

```csharp
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>DB/UI sind immer an; die Zugangsschranke hängt nur noch an Naudit:AccessGate:Mode
/// (Open = AllowAll = Pre-WebUI-Verhalten, Registered = EfAccessGate).</summary>
public class DbWiringTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public DbWiringTests(TestAppFactory factory) => _factory = factory;

    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditDatabase(config);
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DefaultMode_Open_registriertAllowAllGate_undEfSink()
    {
        using var sp = Build(new() { ["Naudit:Db:ConnectionString"] = "Data Source=unused.db" });
        using var scope = sp.CreateScope();
        Assert.IsType<AllowAllAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
        Assert.IsType<EfReviewAuditSink>(scope.ServiceProvider.GetRequiredService<IReviewAuditSink>());
    }

    [Fact]
    public void ModeRegistered_registriertEfGate()
    {
        using var sp = Build(new()
        {
            ["Naudit:Db:ConnectionString"] = "Data Source=unused.db",
            ["Naudit:AccessGate:Mode"] = "Registered",
        });
        using var scope = sp.CreateScope();
        Assert.IsType<EfAccessGate>(scope.ServiceProvider.GetRequiredService<IAccessGate>());
    }

    [Fact]
    public async Task UiEndpoints_sindImmerGemappt()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitLab");
            b.UseSetting("Naudit:GitLab:WebhookSecret", "s");
        }).CreateClient();

        // /api/me existiert jetzt immer — 401 (nicht eingeloggt) statt 404 (nicht gemappt).
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
    }
}
```

(`Microsoft.AspNetCore.Hosting` für `UseSetting` importieren, falls der Compiler es verlangt.)

- [ ] **Step 2: Rot bestätigen** — `dotnet test Naudit.slnx --filter DbWiringTests` ⇒ Compile-Fehler (`AddNauditDatabase` fehlt).

- [ ] **Step 3: Options & DI umbauen.**

`DatabaseOptions.cs`: Property `Enabled` **ersatzlos löschen**, Default-ConnectionString ändern, XML-Doc anpassen:

```csharp
/// <summary>Config-Section Naudit:Db — Naudits Persistenz (Settings, Zugangsschranke, Audit-Log,
/// Accounts, Data-Protection-Keys). Die DB ist PFLICHT (Bootstrap-Keys, env-only): SQLite ist der
/// Zero-Config-Default (relativer Pfad für den Binary-Fall; das Dockerfile setzt /data/naudit.db).</summary>
public sealed class DatabaseOptions
{
    /// <summary>DB-Backend: SQLite (Default) oder Postgres (externe DB).</summary>
    public DbProvider Provider { get; set; } = DbProvider.Sqlite;

    public string ConnectionString { get; set; } = "Data Source=data/naudit.db";
    // ... ConfigureDbContext aus Task 3 bleibt ...
}
```

`UiOptions.cs`: Property `Enabled` löschen; XML-Doc der Klasse ersetzen durch:

```csharp
/// <summary>Config-Section Naudit:Ui — WebUI-Belange (Seed-Admin, Admin-Liste, Sign-in-Provider).
/// Die UI selbst ist immer an (sie ist die Konfigurationsoberfläche).</summary>
```

Neu `src/Naudit.Infrastructure/Ui/AccessGateOptions.cs`:

```csharp
namespace Naudit.Infrastructure.Ui;

/// <summary>Naudit:AccessGate — Open (Default): jedes Projekt mit gültigem Webhook-Secret wird
/// reviewt (Pre-WebUI-Verhalten, typisch internes GitLab). Registered: nur Projekte aktiver
/// Accounts (EfAccessGate) — empfohlen für öffentlich installierbare GitHub Apps.</summary>
public enum AccessGateMode { Open, Registered }

public sealed class AccessGateOptions
{
    public AccessGateMode Mode { get; set; } = AccessGateMode.Open;
}
```

`NullReviewAuditSink.cs` löschen. In `AllowAllAccessGate.cs` den XML-Doc auf den neuen Zweck anpassen (`AccessGate:Mode=Open` statt „DB aus").

`DependencyInjection.cs`:
1. Neue Methode **vor** `AddNauditInfrastructure` einfügen:

```csharp
    /// <summary>DB-Basis (immer an): Options, DbContext, Settings- und Account-Service.
    /// Getrennt von AddNauditInfrastructure, damit der Recovery-Modus (kaputte Review-Config)
    /// die DB/UI-Basis trotzdem bekommt.</summary>
    public static IServiceCollection AddNauditDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        var dbOptions = configuration.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();
        services.AddSingleton(dbOptions);
        services.AddDbContext<NauditDbContext>(o => DatabaseOptions.ConfigureDbContext(o, dbOptions));
        services.AddScoped<Settings.SettingsService>();
        services.AddScoped<AccountService>();
        return services;
    }
```

2. In `AddNauditInfrastructure` den kompletten Block ab „Persistenz (Naudit:Db)" bis zum Ende des `if (uiOptions.Enabled)`-Blocks ersetzen durch:

```csharp
        var uiOptions = configuration.GetSection("Naudit:Ui").Get<UiOptions>() ?? new UiOptions();
        services.AddSingleton(uiOptions);

        // Zugangsschranke: explizite Betriebsart statt (wie früher) implizit an der DB zu hängen.
        var gateOptions = configuration.GetSection("Naudit:AccessGate").Get<AccessGateOptions>() ?? new AccessGateOptions();
        services.AddSingleton(gateOptions);
        if (gateOptions.Mode == AccessGateMode.Registered)
            services.AddScoped<IAccessGate, EfAccessGate>();
        else
            services.AddSingleton<IAccessGate>(new AllowAllAccessGate());
        services.AddScoped<IReviewAuditSink, EfReviewAuditSink>();
```

(Der `dbOptions`-Get am Anfang des alten Blocks entfällt hier — DbContext kommt aus `AddNauditDatabase`.)

- [ ] **Step 4: Program.cs minimal anpassen** (nur Enabled-Verzweigungen; keine Schleife):
  - Nach `var builder = WebApplication.CreateBuilder(args);`: `builder.Services.AddNauditDatabase(builder.Configuration);` einfügen (vor `AddNauditInfrastructure`).
  - `if (uiConfig.Enabled)`-Block um Auth/DataProtection (Zeilen ~26–124): Bedingung entfernen, Inhalt bleibt (Auth immer registrieren). Am DataProtection-Aufruf `.SetApplicationName(Naudit.Infrastructure.Settings.DbSettingsLoader.DataProtectionAppName)` anhängen.
  - Migrations-/Seed-Block: `if (dbOptions.Enabled)` und inneres `if (uiConfig.Enabled)` entfernen — migrieren + `SeedAsync()` laufen immer.
  - `if (uiConfig.Enabled)` um `UseAuthentication/UseAuthorization`: Bedingung entfernen.
  - `if (uiConfig.Enabled)` um `MapAuthEndpoints/MapAdminEndpoints/MapDataEndpoints` + SPA: Bedingung entfernen.

- [ ] **Step 5: Betroffene Tests anpassen.** Mechanik pro Datei — vorher jeweils lesen:
  - `DatabaseOptionsTests.cs`: Asserts auf `Enabled` löschen; Default-ConnectionString-Assert auf `"Data Source=data/naudit.db"` ändern.
  - `UiOptionsTests.cs`: Asserts/Bindings auf `Ui:Enabled` löschen (Rest der Options-Bindung bleibt).
  - `ReviewAuditSinkTests.cs`: `[Fact]`s, die `NullReviewAuditSink` instanziieren/asserten, löschen (Ef-Tests bleiben). Finden mit `grep -n NullReviewAuditSink tests/Naudit.Tests/ReviewAuditSinkTests.cs`.
  - `AccessGateEndpointTests.cs`: Bei jedem `WithWebHostBuilder` `b.UseSetting("Naudit:AccessGate:Mode", "Registered");` ergänzen (die Tests erwarten Gating); `UseSetting("Naudit:Db:Enabled", …)`/`("Naudit:Ui:Enabled", …)` entfernen.
  - `SpaHostingTests.cs`: Tests der Art „UI aus ⇒ 404" löschen oder in „immer an"-Asserts drehen (analog `UiEndpoints_sindImmerGemappt`); `Ui:Enabled`/`Db:Enabled`-Settings entfernen.
  - `AuthEndpointTests.cs`, `ExternalAuthTests.cs`, `AdminEndpointTests.cs`, `DataEndpointTests.cs`, `WebhookEndpointTests.cs`, `ReviewEndpointTests.cs`: `UseSetting("Naudit:Ui:Enabled", …)` / `("Naudit:Db:Enabled", …)`-Zeilen entfernen (jetzt wirkungslos). Verifikation: `grep -rn "Ui:Enabled\|Db:Enabled" tests/ src/` ⇒ keine Treffer mehr.

- [ ] **Step 6: Volle Suite grün** — `dotnet test Naudit.slnx` ⇒ PASS. (Falls Webhook-/Review-Tests jetzt am Gate scheitern: Default muss `Open` sein — Registrierung in Step 3 prüfen.)

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat!: DB und WebUI immer an; Zugangsschranke explizit ueber Naudit:AccessGate:Mode"
```

---

### Task 7: Host-Schleife, IAppRestarter, DB-Config-Bootstrap, Recovery-Modus

**Files:**
- Create: `src/Naudit.Web/AppRestarter.cs`
- Modify: `src/Naudit.Web/Program.cs`
- Test: `tests/Naudit.Tests/AppRestarterTests.cs`, `tests/Naudit.Tests/RecoveryModeTests.cs`

**Interfaces:**
- Consumes: `DbSettingsLoader.Load`, `NauditConfig.InsertDbSettings`, `AddNauditDatabase` (Tasks 3–6).
- Produces:
  - `public interface IAppRestarter { void RequestRestart(); void MarkRestartPending(); bool RestartPending { get; } }` (Namespace `Naudit.Web`)
  - `public sealed class AppRestarter : IAppRestarter { void Attach(IHostApplicationLifetime lifetime); bool ConsumeRestartRequest(); }`
  - Program stellt als DI-Singletons bereit: `IAppRestarter`, `EnvOverrides`, `StartupState` (`sealed record StartupState(string? RecoveryError, IReadOnlyList<string> Warnings)` in `AppRestarter.cs` mit definiert) — Task 8 konsumiert alle drei.
  - Verhalten: Config-Fehler in der Infrastruktur-Registrierung ⇒ Recovery-Modus (UI/Settings an, Webhooks + `/review` + Background-Service aus).

- [ ] **Step 1: AppRestarter-Unit-Test (rot)** — `tests/Naudit.Tests/AppRestarterTests.cs`:

```csharp
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class AppRestarterTests
{
    [Fact]
    public void ConsumeRestartRequest_liefertEinmalTrue_undResettet()
    {
        var r = new AppRestarter();
        Assert.False(r.ConsumeRestartRequest()); // ohne Request: kein Neustart
        r.RequestRestart();                       // ohne Attach: wirft nicht (Host evtl. noch nicht da)
        Assert.True(r.ConsumeRestartRequest());
        Assert.False(r.ConsumeRestartRequest());  // verbraucht
    }

    [Fact]
    public void MarkRestartPending_setztFlag()
    {
        var r = new AppRestarter();
        Assert.False(r.RestartPending);
        r.MarkRestartPending();
        Assert.True(r.RestartPending);
    }
}
```

- [ ] **Step 2: Rot** — `dotnet test Naudit.slnx --filter AppRestarterTests` ⇒ Compile-Fehler.

- [ ] **Step 3: AppRestarter implementieren** — `src/Naudit.Web/AppRestarter.cs`:

```csharp
namespace Naudit.Web;

/// <summary>Kontrollierter In-Process-Neustart: Endpoints rufen RequestRestart, der Host stoppt,
/// die Schleife in Program.cs baut ihn neu (Config-Änderungen aus der DB werden so übernommen).</summary>
public interface IAppRestarter
{
    void RequestRestart();

    /// <summary>Merkt „Settings geändert, Neustart steht aus" — fürs Banner der Settings-Seite.</summary>
    void MarkRestartPending();

    bool RestartPending { get; }
}

public sealed class AppRestarter : IAppRestarter
{
    private volatile bool _restartRequested;
    private volatile bool _restartPending;
    private IHostApplicationLifetime? _lifetime;

    /// <summary>Nach dem Host-Bau aufrufen — vorher läuft RequestRestart ins Leere (nur Flag).</summary>
    public void Attach(IHostApplicationLifetime lifetime) => _lifetime = lifetime;

    public void RequestRestart()
    {
        _restartRequested = true;
        _lifetime?.StopApplication();
    }

    public void MarkRestartPending() => _restartPending = true;
    public bool RestartPending => _restartPending;

    /// <summary>Von der Program-Schleife nach RunAsync gelesen; setzt beide Flags zurück.</summary>
    public bool ConsumeRestartRequest()
    {
        var requested = _restartRequested;
        _restartRequested = false;
        _restartPending = false;
        return requested;
    }
}

/// <summary>Startzustand für die Settings-API: Recovery-Fehler (Config kaputt) + Loader-Warnungen
/// (z. B. nicht entschlüsselbare Secrets).</summary>
public sealed record StartupState(string? RecoveryError, IReadOnlyList<string> Warnings);
```

- [ ] **Step 4: Program.cs umbauen.** Zielstruktur (bestehende Blöcke wandern unverändert in die lokale Funktion; hier nur das neue Gerüst und die neuen/geänderten Stellen):

```csharp
var restarter = new AppRestarter();
while (true)
{
    var app = BuildApp(args, restarter);
    // Seed läuft immer (auch Recovery): der Admin muss sich einloggen können, um zu reparieren.
    using (var scope = app.Services.CreateScope())
        await scope.ServiceProvider.GetRequiredService<Naudit.Infrastructure.Ui.AccountService>().SeedAsync();
    restarter.Attach(app.Lifetime);
    await app.RunAsync();
    if (!restarter.ConsumeRestartRequest()) break;
}

static WebApplication BuildApp(string[] args, AppRestarter restarter)
{
    var builder = WebApplication.CreateBuilder(args);

    // 1) Bootstrap: DB migrieren, Settings laden, als Config-Quelle unter Env einhängen.
    var dbOptions = builder.Configuration.GetSection("Naudit:Db").Get<DatabaseOptions>() ?? new DatabaseOptions();
    var load = DbSettingsLoader.Load(dbOptions);
    var envOverrides = NauditConfig.InsertDbSettings(builder.Configuration, load.Settings);

    // 2) Probe: registriert die Review-Infrastruktur in einen WEGWERF-Container. Wirft sie
    //    (z. B. Auth=App ohne PrivateKey), starten wir im Recovery-Modus statt in der Crash-Loop.
    Exception? configError = null;
    try
    {
        var probe = new ServiceCollection();
        probe.AddLogging();
        probe.AddNauditInfrastructure(builder.Configuration);
    }
    catch (Exception ex) { configError = ex; }

    // 3) Basis immer: DB, Auth/Cookies, DataProtection, UI. Review-Teile nur bei gesunder Config.
    builder.Services.AddSingleton<IAppRestarter>(restarter);
    builder.Services.AddSingleton(envOverrides);
    builder.Services.AddSingleton(new StartupState(configError?.Message, load.Warnings));
    builder.Services.AddNauditDatabase(builder.Configuration);
    if (configError is null)
    {
        builder.Services.AddNauditInfrastructure(builder.Configuration);
        builder.Services.AddSingleton<IReviewQueue, ReviewQueue>();
        builder.Services.AddHostedService<ReviewBackgroundService>();
    }

    var uiConfig = builder.Configuration.GetSection("Naudit:Ui").Get<UiOptions>() ?? new UiOptions();
    // ... bestehender Auth/Cookie/OAuth/OIDC/DataProtection-Block (aus Task 6, immer aktiv) ...

    var app = builder.Build();
    // ... bestehender ForwardedHeaders-Block ...
    // Migration passiert im DbSettingsLoader — der alte MigrateAsync-Block entfällt ersatzlos.
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapGet("/health", () => Results.Ok("healthy"));

    if (configError is null)
    {
        // ... bestehender Webhook-Block (GitHub XOR GitLab) und POST /review ...
    }
    else
    {
        app.Logger.LogError("Recovery-Modus: {Error} — Webhooks/Review sind deaktiviert, " +
            "Korrektur über die Settings-Seite, dann Neustart.", configError.Message);
    }
    foreach (var warning in load.Warnings) app.Logger.LogWarning("{Warning}", warning);

    // UI immer (auch Recovery — sie ist das Reparaturwerkzeug):
    app.MapAuthEndpoints(uiConfig);
    app.MapAdminEndpoints();
    app.MapDataEndpoints();
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallback("/api/{**rest}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
    return app;
}
```

Wichtig: `Program.cs` behält Top-Level-Statements; `BuildApp` wird lokale statische Funktion **unter** der Schleife (vor den Record-Deklarationen am Dateiende). Der alte `MigrateAsync`/`SeedAsync`-Block und `IsValidNauditToken` etc. bleiben inhaltlich erhalten (nur verschoben). `GetSection("Naudit:Ui")` **nach** `InsertDbSettings` lesen (damit DB-Werte greifen) — das erledigt die Reihenfolge oben.

- [ ] **Step 5: Recovery-WAF-Test (rot → grün)** — `tests/Naudit.Tests/RecoveryModeTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

/// <summary>Kaputte Review-Config (GitHub App ohne Keys) ⇒ kein Crash, sondern Recovery:
/// Health + UI laufen, Webhooks/Review sind nicht gemappt.</summary>
public class RecoveryModeTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public RecoveryModeTests(TestAppFactory factory) => _factory = factory;

    [Fact]
    public async Task KaputteConfig_startetRecoveryStattCrash()
    {
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Git:Platform", "GitHub");
            b.UseSetting("Naudit:GitHub:Auth", "App"); // AppId/PrivateKey fehlen ⇒ Registrierung wirft
        }).CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/webhook/github", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/webhook/gitlab", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode); // UI lebt
    }
}
```

- [ ] **Step 6: Volle Suite grün** — `dotnet test Naudit.slnx` ⇒ PASS. Danach Rauchtest von Schleife/Neustart manuell: `dotnet run --project src/Naudit.Web --urls http://localhost:5080` starten, `curl http://localhost:5080/health` ⇒ `healthy` (der echte Neustart wird nach Task 8 über den Restart-Endpoint verifiziert).

- [ ] **Step 7: Commit**

```bash
git add src/Naudit.Web tests/Naudit.Tests
git commit -m "feat(web): Host-Schleife mit IAppRestarter, DB-Config-Bootstrap und Recovery-Modus"
```

---

### Task 8: Settings-API — GET (Katalog + Quelle), PUT (write-only Secrets), POST restart

**Files:**
- Create: `src/Naudit.Web/Endpoints/SettingsEndpoints.cs`
- Modify: `src/Naudit.Web/Endpoints/DataEndpoints.cs` (alten `/api/settings`-Block entfernen)
- Modify: `src/Naudit.Web/Program.cs` (`app.MapSettingsEndpoints();` neben den anderen UI-Endpoints)
- Test: `tests/Naudit.Tests/SettingsEndpointTests.cs`

**Interfaces:**
- Consumes: `SettingsCatalog`, `SettingsService`, `EnvOverrides`, `IAppRestarter`, `StartupState` (Tasks 2, 4, 7).
- Produces (JSON-Kontrakt für Task 9):
  - `GET /api/settings` (Admin) ⇒ `{ recoveryError: string|null, warnings: string[], restartPending: bool, settings: [{ key, isSecret, isSet, source: "db"|"env"|"default", editable, value: string|null }] }` — `value` ist bei Secrets **immer** `null`
  - `PUT /api/settings` (Admin), Body `{ changes: [{ key: string, value: string|null }] }` — `value=null` ⇒ Reset (DB-Zeile löschen); unbekannter oder env-gesperrter Key ⇒ 400 mit `{ error }`; Erfolg ⇒ `{ restartPending: true }`
  - `POST /api/settings/restart` (Admin) ⇒ 204, ruft `IAppRestarter.RequestRestart()`

- [ ] **Step 1: Failing Tests** — `tests/Naudit.Tests/SettingsEndpointTests.cs`. Login-Helfer aus `DataEndpointTests.cs` übernehmen (dort nachsehen: Seed-Admin via `UseSetting("Naudit:Ui:Admin:Username", …)`/`InitialPassword` + `POST /auth/login`; denselben Mechanismus hier verwenden):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Tests.Fakes;
using Naudit.Web;
using Xunit;

namespace Naudit.Tests;

public class SettingsEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public SettingsEndpointTests(TestAppFactory factory) => _factory = factory;

    private sealed class FakeRestarter : IAppRestarter
    {
        public int RestartCalls;
        public bool RestartPending { get; private set; }
        public void RequestRestart() => RestartCalls++;
        public void MarkRestartPending() => RestartPending = true;
    }

    private (HttpClient Client, FakeRestarter Restarter) CreateLoggedInAdmin()
    {
        var restarter = new FakeRestarter();
        var client = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Ui:Admin:Username", "admin");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "pw-123456");
            b.ConfigureServices(s => s.AddSingleton<IAppRestarter>(restarter));
        }).CreateClient();
        var login = client.PostAsJsonAsync("/auth/login", new { username = "admin", password = "pw-123456" }).Result;
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (client, restarter);
    }

    [Fact]
    public async Task Get_liefertKatalog_ohneSecretWerte()
    {
        var (client, _) = CreateLoggedInAdmin();
        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var settings = doc.RootElement.GetProperty("settings").EnumerateArray().ToList();
        Assert.Contains(settings, s => s.GetProperty("key").GetString() == "Naudit:Ai:Provider");
        // Secrets: value ist IMMER null, egal ob gesetzt.
        Assert.All(settings.Where(s => s.GetProperty("isSecret").GetBoolean()),
            s => Assert.Equal(JsonValueKind.Null, s.GetProperty("value").ValueKind));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("recoveryError").ValueKind);
    }

    [Fact]
    public async Task Put_speichertWert_undGetZeigtIhnAlsDbQuelle()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Ai:Model", value = (string?)"claude-sonnet-5" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(restarter.RestartPending);

        var doc = JsonDocument.Parse(await client.GetStringAsync("/api/settings"));
        var model = doc.RootElement.GetProperty("settings").EnumerateArray()
            .Single(s => s.GetProperty("key").GetString() == "Naudit:Ai:Model");
        Assert.Equal("db", model.GetProperty("source").GetString());
        Assert.True(model.GetProperty("isSet").GetBoolean());
        // GET zeigt hier noch den ALTEN effektiven Wert (IConfiguration lädt DB erst beim Neustart) —
        // die UI kommuniziert das über restartPending; deshalb kein Assert auf value.
    }

    [Fact]
    public async Task Put_secret_wirdGespeichertAberNieZurueckgegeben()
    {
        var (client, _) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Ai:ApiKey", value = (string?)"sk-geheim" } },
        });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await client.GetStringAsync("/api/settings");
        Assert.DoesNotContain("sk-geheim", body);
    }

    [Fact]
    public async Task Put_unbekannterKey_gibt400()
    {
        var (client, _) = CreateLoggedInAdmin();
        var res = await client.PutAsJsonAsync("/api/settings", new
        {
            changes = new[] { new { key = "Naudit:Db:ConnectionString", value = (string?)"x" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Restart_ruftRestarter_undGibt204()
    {
        var (client, restarter) = CreateLoggedInAdmin();
        var res = await client.PostAsync("/api/settings/restart", null);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(1, restarter.RestartCalls);
    }

    [Fact]
    public async Task NichtAdmin_bekommt401Oder403()
    {
        var client = _factory.CreateClient(); // nicht eingeloggt
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/settings")).StatusCode);
    }
}
```

(Login-Body-Feldnamen ggf. an `AuthEndpoints.cs` anpassen — vor dem Schreiben dort nachsehen.)

- [ ] **Step 2: Rot** — `dotnet test Naudit.slnx --filter SettingsEndpointTests`.

- [ ] **Step 3: Implementieren** — `SettingsEndpoints.cs`:

```csharp
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Settings;

namespace Naudit.Web.Endpoints;

/// <summary>Editierbare Settings (Admin): GET zeigt Katalog + Quelle (db/env/default), PUT schreibt
/// in die DB (Secrets write-only), POST restart übernimmt per Host-Neustart. Env-gesetzte Keys
/// sind gesperrt — env gewinnt immer über DB, die UI macht das sichtbar statt verwirrend.</summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();

        group.MapGet("/", async (HttpContext ctx, NauditDbContext db, SettingsService settings,
            IConfiguration config, EnvOverrides env, IAppRestarter restarter, StartupState startup) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            var dbKeys = await settings.GetSetKeysAsync(ctx.RequestAborted);

            return Results.Ok(new
            {
                recoveryError = startup.RecoveryError,
                warnings = startup.Warnings,
                restartPending = restarter.RestartPending,
                settings = SettingsCatalog.All.Select(def =>
                {
                    var envLocked = env.Root[def.Key] is not null;
                    var isSet = envLocked || dbKeys.Contains(def.Key) || config[def.Key] is not null;
                    return new
                    {
                        key = def.Key,
                        isSecret = def.IsSecret,
                        isSet,
                        source = envLocked ? "env" : dbKeys.Contains(def.Key) ? "db" : "default",
                        editable = !envLocked,
                        value = def.IsSecret ? null : config[def.Key],
                    };
                }),
            });
        });

        group.MapPut("/", async (HttpContext ctx, NauditDbContext db, SettingsService settings,
            EnvOverrides env, IAppRestarter restarter, UpdateSettingsRequest body) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();

            // Erst komplett validieren, dann schreiben — keine halb angewendeten Batches.
            foreach (var change in body.Changes)
            {
                if (!SettingsCatalog.TryGet(change.Key, out _))
                    return Results.BadRequest(new { error = $"'{change.Key}' is not a managed setting." });
                if (env.Root[change.Key] is not null)
                    return Results.BadRequest(new { error = $"'{change.Key}' is set via environment and cannot be edited here." });
            }
            foreach (var change in body.Changes)
            {
                if (change.Value is null) await settings.RemoveAsync(change.Key, ctx.RequestAborted);
                else await settings.SetAsync(change.Key, change.Value, ctx.RequestAborted);
            }
            restarter.MarkRestartPending();
            return Results.Ok(new { restartPending = true });
        });

        group.MapPost("/restart", async (HttpContext ctx, NauditDbContext db, IAppRestarter restarter) =>
        {
            if (await CurrentAccount.GetAdminAsync(ctx, db) is null) return Results.Forbid();
            restarter.RequestRestart();
            return Results.NoContent();
        });
    }
}

public sealed record SettingChange(string Key, string? Value);
public sealed record UpdateSettingsRequest(List<SettingChange> Changes);
```

In `DataEndpoints.cs` den `api.MapGet("/settings", …)`-Block samt Kommentar löschen (die zugehörigen Usings `Naudit.Core.Review`, `Naudit.Infrastructure.Ai`, `Naudit.Infrastructure.Git*`, `Microsoft.Extensions.Options` entfernen, sofern sonst ungenutzt). In `Program.cs` `app.MapSettingsEndpoints();` direkt nach `app.MapDataEndpoints();` einfügen. In `DataEndpointTests.cs` Tests des alten `/api/settings`-Formats löschen (Ersatz sind die neuen SettingsEndpointTests).

- [ ] **Step 4: Grün + Suite** — `dotnet test Naudit.slnx` ⇒ PASS.

- [ ] **Step 5: Manuelle End-to-End-Verifikation des Neustarts** (jetzt möglich):

```bash
dotnet run --project src/Naudit.Web --urls http://localhost:5080 &
sleep 3 && curl -s http://localhost:5080/health
# Login + Restart über die API (Cookie-Jar), danach muss /health wieder antworten:
curl -s -c /tmp/naudit-cookies -X POST http://localhost:5080/auth/login -H "Content-Type: application/json" -d '{"username":"<seed-admin>","password":"<pw>"}'
curl -s -b /tmp/naudit-cookies -X POST http://localhost:5080/api/settings/restart -w "%{http_code}\n"
sleep 3 && curl -s http://localhost:5080/health   # -> healthy  (Prozess lief durch, Host wurde neu gebaut)
```

Expected: zweites `/health` antwortet `healthy` ohne Prozess-Neustart. (Seed-Admin vorher per env setzen: `Naudit__Ui__Admin__Username=admin Naudit__Ui__Admin__InitialPassword=pw-123456 dotnet run …`.)

- [ ] **Step 6: Commit**

```bash
git add src/Naudit.Web tests/Naudit.Tests
git commit -m "feat(web): editierbare Settings-API (Quelle/Locks/write-only Secrets) + Restart-Endpoint"
```

---

### Task 9: Frontend — Settings-Seite editierbar

**Files:**
- Modify: `src/frontend/src/api/types.ts` (alte `SettingsDto` ersetzen)
- Modify: `src/frontend/src/hooks/queries.ts` (`useSettings` anpassen; `useSaveSettings`, `useRestartApp` ergänzen)
- Modify: `src/frontend/src/components/pages/SettingsPage.tsx` (komplett neu)

**Interfaces:**
- Consumes: JSON-Kontrakt aus Task 8.
- Produces: editierbare Settings-Seite; keine neuen Komponenten-Exporte über die Seite hinaus.

Hinweis: Der Branch `feat/webui-reactivity` (PR mit TanStack-Mutations) ist evtl. schon in `main` — vor Beginn `git log origin/main --oneline -3` prüfen und ggf. rebasen; falls `hooks/queries.ts` dann bereits Mutations enthält, deren Stil übernehmen.

- [ ] **Step 1: Typen** — in `types.ts` die bisherige `SettingsDto` ersetzen durch:

```ts
export type SettingItem = {
  key: string;
  isSecret: boolean;
  isSet: boolean;
  source: "db" | "env" | "default";
  editable: boolean;
  value: string | null;
};

export type SettingsDto = {
  recoveryError: string | null;
  warnings: string[];
  restartPending: boolean;
  settings: SettingItem[];
};
```

- [ ] **Step 2: Hooks** — in `queries.ts` ergänzen (Imports: `useMutation`, `useQueryClient` aus `@tanstack/react-query`):

```ts
export function useSaveSettings() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (changes: { key: string; value: string | null }[]) =>
      api<{ restartPending: boolean }>("/api/settings", {
        method: "PUT",
        body: JSON.stringify({ changes }),
      }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["settings"] }),
  });
}

export function useRestartApp() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>("/api/settings/restart", { method: "POST" }),
    // Host braucht ~2 s zum Neustart; danach Settings neu laden (Session-Cookie überlebt, DP-Keys in DB).
    onSuccess: () =>
      new Promise((resolve) => setTimeout(resolve, 2500)).then(() =>
        qc.invalidateQueries({ queryKey: ["settings"] })),
  });
}
```

- [ ] **Step 3: SettingsPage neu schreiben** — `SettingsPage.tsx` ersetzen (Design-Sprache der bestehenden Seite: `Panel`, `Pill`, Tailwind-Tokens `ink/ink2/ink3/hairline` weiterverwenden):

```tsx
import { useMemo, useState } from "react";
import { useRestartApp, useSaveSettings, useSettings } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";
import type { SettingItem } from "@/api/types";

/** Gruppierung + Reihenfolge der Panels; Keys wie im Backend-Katalog. */
const GROUPS: { title: string; extra: string; prefixes: string[] }[] = [
  { title: "General", extra: "Naudit", prefixes: ["Naudit:PublicBaseUrl", "Naudit:AccessGate:"] },
  { title: "Git platform", extra: "Naudit:Git*", prefixes: ["Naudit:Git:", "Naudit:GitLab:", "Naudit:GitHub:"] },
  { title: "AI provider", extra: "Naudit:Ai", prefixes: ["Naudit:Ai:"] },
  { title: "Review", extra: "Naudit:Review", prefixes: ["Naudit:Review:"] },
  { title: "Sign-in", extra: "Naudit:Ui:Auth", prefixes: ["Naudit:Ui:Auth:"] },
];

/** Enum-Keys werden als Select gerendert statt als Freitext. */
const ENUMS: Record<string, string[]> = {
  "Naudit:Git:Platform": ["GitLab", "GitHub"],
  "Naudit:GitHub:Auth": ["Pat", "App"],
  "Naudit:Ai:Provider": ["Ollama", "Anthropic", "OpenAICompatible", "ClaudeCode"],
  "Naudit:AccessGate:Mode": ["Open", "Registered"],
  "Naudit:Review:Gate:MinSeverity": ["Info", "Low", "Medium", "High", "Critical"],
  "Naudit:Review:Gate:MinConfidence": ["Low", "Medium", "High"],
  "Naudit:Ui:Auth:GitHub:Enabled": ["false", "true"],
  "Naudit:Ui:Auth:Oidc:Enabled": ["false", "true"],
};

function SettingRow({ item, draft, onChange }: {
  item: SettingItem;
  draft: string | undefined;
  onChange: (v: string) => void;
}) {
  const label = item.key.replace(/^Naudit:/, "");
  const current = draft ?? item.value ?? "";
  const options = ENUMS[item.key];
  return (
    <div className="flex items-center justify-between gap-4 border-b border-hairline px-5 py-3 last:border-b-0">
      <span className="flex items-center gap-2 text-[13px] font-medium text-ink">
        {label}
        {item.source === "env" && <Pill kind="neutral">via environment</Pill>}
        {item.source === "db" && <Pill kind="ok">db</Pill>}
      </span>
      {!item.editable ? (
        <span className="font-mono text-[12.5px] text-ink3">{item.isSecret ? "•••" : (item.value ?? "—")}</span>
      ) : options ? (
        <select
          className="rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          value={current} onChange={(e) => onChange(e.target.value)}
        >
          <option value="">(default)</option>
          {options.map((o) => <option key={o} value={o}>{o}</option>)}
        </select>
      ) : (
        <input
          type={item.isSecret ? "password" : "text"}
          className="w-64 rounded border border-hairline bg-transparent px-2 py-1 font-mono text-[12.5px] text-ink2"
          placeholder={item.isSecret ? (item.isSet ? "•••••• (set)" : "not set") : ""}
          value={draft ?? (item.isSecret ? "" : (item.value ?? ""))}
          onChange={(e) => onChange(e.target.value)}
        />
      )}
    </div>
  );
}

/** Editierbar (Admin): schreibt in die DB; env-gesetzte Keys sind gesperrt. Änderungen
 *  gelten erst nach dem Neustart — Banner + Restart-Button. Secrets sind write-only. */
export function SettingsPage() {
  const { data, isLoading } = useSettings();
  const save = useSaveSettings();
  const restart = useRestartApp();
  const [drafts, setDrafts] = useState<Record<string, string>>({});

  const dirty = useMemo(() => Object.keys(drafts).length > 0, [drafts]);
  if (isLoading || !data) return <div className="p-8 font-mono text-ink3">loading…</div>;

  const onSave = () => {
    const changes = Object.entries(drafts).map(([key, value]) => ({ key, value: value === "" ? null : value }));
    save.mutate(changes, { onSuccess: () => setDrafts({}) });
  };

  return (
    <div className="flex flex-col gap-5 px-7 py-6">
      <div className="flex items-start justify-between">
        <div>
          <h2 className="font-mono text-lg font-bold">Settings</h2>
          <p className="mt-1 max-w-[70ch] text-[12.5px] text-ink3">
            Stored in Naudit's database. Keys set via environment are locked (environment always wins).
            Changes take effect after a restart. Clearing a field resets it to its default.
          </p>
        </div>
        <div className="flex gap-2">
          <button onClick={onSave} disabled={!dirty || save.isPending}
            className="rounded bg-accent px-3 py-1.5 font-mono text-[12.5px] font-bold text-black disabled:opacity-40">
            {save.isPending ? "saving…" : "Save changes"}
          </button>
        </div>
      </div>

      {data.recoveryError && (
        <div className="rounded border border-red-500/40 bg-red-500/10 px-4 py-3 text-[12.5px] text-red-300">
          <b>Recovery mode:</b> {data.recoveryError} — reviews are paused until fixed &amp; restarted.
        </div>
      )}
      {data.warnings.map((w) => (
        <div key={w} className="rounded border border-yellow-500/40 bg-yellow-500/10 px-4 py-3 text-[12.5px] text-yellow-200">{w}</div>
      ))}
      {data.restartPending && (
        <div className="flex items-center justify-between rounded border border-hairline bg-white/5 px-4 py-3 text-[12.5px] text-ink2">
          <span>Pending changes — restart Naudit to apply.</span>
          <button onClick={() => restart.mutate()} disabled={restart.isPending}
            className="rounded border border-hairline px-3 py-1 font-mono disabled:opacity-40">
            {restart.isPending ? "restarting…" : "Restart now"}
          </button>
        </div>
      )}

      <div className="grid grid-cols-1 items-start gap-4 md:grid-cols-2">
        {GROUPS.map((g) => (
          <Panel key={g.title} title={g.title} extra={g.extra}>
            {data.settings
              .filter((s) => g.prefixes.some((p) => s.key === p || s.key.startsWith(p)))
              .map((s) => (
                <SettingRow key={s.key} item={s} draft={drafts[s.key]}
                  onChange={(v) => setDrafts((d) => ({ ...d, [s.key]: v }))} />
              ))}
          </Panel>
        ))}
      </div>
    </div>
  );
}
```

(Falls es die Tailwind-Klasse `bg-accent` im Projekt nicht gibt: die Akzentfarbe der bestehenden Buttons — z. B. auf der Approvals-Seite — nachschlagen und dieselbe Klasse verwenden.)

- [ ] **Step 4: Lint + Build**

```bash
cd src/frontend && npm ci && npm run lint && npm run build
```

Expected: beides grün (`build` = `tsc --noEmit && vite build`).

- [ ] **Step 5: Manueller Rauchtest** — Backend starten (`dotnet run --project src/Naudit.Web` mit Seed-Admin-Env), `npm run dev`, im Browser: Settings öffnen, Wert ändern → Save → Banner erscheint → Restart now → nach ~3 s lädt die Seite die Settings neu, Quelle des Werts zeigt `db`, der neue Wert ist der effektive.

- [ ] **Step 6: Commit**

```bash
git add src/frontend
git commit -m "feat(webui): Settings-Seite editierbar (env-Locks, write-only Secrets, Restart-Banner)"
```

---

### Task 10: Dockerfile-ENV + Docs

**Files:**
- Modify: `Dockerfile` (Container-Default für den DB-Pfad)
- Modify: `docs/configuration.md`, `docs/deployment.md`, `docs/webui.md`, `CLAUDE.md`

- [ ] **Step 1: Dockerfile** — im finalen Runtime-Stage (bei `EXPOSE 8080`, vor `ENTRYPOINT`) ergänzen:

```dockerfile
# DB-Pflicht: im Container liegt die SQLite-Default-DB auf dem /data-Volume
# (der App-Default "data/naudit.db" ist fuer den Binary-Fall gedacht).
ENV Naudit__Db__ConnectionString="Data Source=/data/naudit.db"
```

- [ ] **Step 2: Docs (englisch).** Kernänderungen — bestehende Struktur beibehalten, betroffene Passagen umschreiben:
  - `docs/configuration.md`: Neuer Einleitungsabschnitt „Where configuration lives": DB-managed settings (editable in the UI) vs. **bootstrap keys** (env-only: `Naudit:Db:*`, `Naudit:ForwardedHeaders:*`, ports) — precedence `appsettings < database < user-secrets/env vars`; env-set keys show as locked in the UI. Tabelle: Zeilen `Naudit:Db:Enabled`/`Naudit:Ui:Enabled` entfernen, `Naudit:AccessGate:Mode` (`Open` default | `Registered`) und `Naudit:PublicBaseUrl` ergänzen; Hinweis bei den Secrets, dass sie alternativ in der UI gepflegt werden (encrypted at rest; honest note: key ring shares the database).
  - `docs/deployment.md`: Env-Template kürzen — Pflicht bleibt nur der Bootstrap-Teil (+ optional alles andere als Override); **Breaking-Box**: `Naudit__Db__Enabled`/`Naudit__Ui__Enabled` removed (values ignored; DB+UI always on — deployments previously running *without* a DB now get a SQLite file, mount `/data`!); deployments that relied on the account gate must set `Naudit__AccessGate__Mode=Registered` (new default is `Open`).
  - `docs/webui.md`: „Two switches control all of this"-Absatz ersetzen (always on); Settings-Abschnitt von „read-only" auf „editable (admin), env wins, restart to apply, secrets write-only" umschreiben; Access-model-Abschnitt: gate nur bei `AccessGate:Mode=Registered`.
  - `CLAUDE.md`: den Spiegelstrich „**DB (first-class concern) + WebUI on top (both opt-in)**" umschreiben: DB+UI immer an, Config-Modell (DB-Settings-Quelle unter env, `DbSettingsLoader`, Host-Schleife/`IAppRestarter`, Recovery-Modus, `AccessGate:Mode`), Verweis auf die Spec.

- [ ] **Step 3: Verifikation** — `dotnet test Naudit.slnx` (unverändert grün) und Docker-Build-Rauchtest, sofern Docker verfügbar: `docker build -t naudit-local . && docker run --rm -p 8080:8080 naudit-local &` → `curl http://localhost:8080/health` ⇒ `healthy` (SQLite entsteht im Container unter `/data` auch ohne Volume — flüchtig, aber lauffähig). Ohne Docker: Schritt dokumentiert lassen und im PR-Text erwähnen.

- [ ] **Step 4: Commit**

```bash
git add Dockerfile docs CLAUDE.md
git commit -m "docs: Config-Modell (DB-managed + Bootstrap-Keys), Breaking-Notes; Dockerfile /data-Default"
```

---

### Task 11: Endabnahme

- [ ] **Step 1: Volle Verifikation**

```bash
dotnet test Naudit.slnx
cd src/frontend && npm run lint && npm run build && cd ../..
grep -rn "Ui:Enabled\|Db:Enabled" src/ tests/ docs/ && echo "LEAK" || echo "clean"
```

Expected: Tests PASS, Frontend grün, `clean` (nur die Breaking-Note in den Docs darf die alten Namen erwähnen — dann gezielt prüfen, dass es Doku und kein Code ist).

- [ ] **Step 2: Manueller E2E-Durchlauf** (Kurzform): frische DB (Datenverzeichnis löschen), Start mit Seed-Admin-Env, Login → Settings → AI-Provider + Modell setzen → Save → Restart → Settings zeigen `db`-Quelle und den effektiven Wert. Danach denselben Key als Env-Var setzen, neu starten → Key ist gesperrt („via environment") und der Env-Wert gewinnt.

- [ ] **Step 3: Push + PR** — gemäß `superpowers:finishing-a-development-branch`; PR-Titel: `feat: config lives in the database — always-on DB/UI, editable settings, restart loop (setup foundation, 1/3)`.
