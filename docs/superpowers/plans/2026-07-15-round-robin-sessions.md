# Round-Robin-Session-Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ein dritter Session-Modus `Naudit:Ai:SessionRouting = Single | Author | RoundRobin`, der die per-Opt-in freigegebenen Claude-Abos sequentiell über die Reviews rotiert.

**Architecture:** Baut auf Author-Sessions (PR #53) auf und wird dessen dritter Modus. Neu sind: eine Opt-in-Spalte `AccountEntity.ShareSessionInPool`, ein `RoundRobinSessionRouter` (Infrastructure, spiegelt `AuthorSessionRouter`) mit einem prozess-globalen `RoundRobinCursor`, die Config-Umschaltung von einem bool (`AuthorSessions:Enabled`) auf ein Enum (`SessionRouting`), und die passenden UI-/Doku-Änderungen. `ClaudeCodeChatClient`, `FallbackChatClient`, `SessionHealthRegistry`, `ClaudeSessionService`, `ReviewEntity.AiSessionAccountId` werden unverändert wiederverwendet.

**Tech Stack:** .NET 10, EF Core (SQLite/Postgres), MEAI, xUnit, React/TS.

**Spec:** `docs/superpowers/specs/2026-07-15-round-robin-sessions-design.md`

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — Tests via `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter ...` bzw. `dotnet test Naudit.slnx`. **Nie** `Naudit.sln`.
- Core-Regel: `Naudit.Core` kennt nur MEAI-Abstractions. Der Router ist Infrastructure; Core hält nur das schon existierende `IAiClientRouter`/`AiClientSelection`. Kein SDK/EF-Verweis in Core.
- **Migration handgepflegt provider-neutral** (wie `20260711151631_AuthorSessions`): Migration-`.cs` und `.Designer.cs` ohne explizite Spaltentypen/`HasColumnType`; der Snapshot `NauditDbContextModelSnapshot.cs` bleibt SQLite-baked. **Kein `dotnet ef migrations add`** — die Migration wird per Copy-und-Anpassen der AuthorSessions-Migration erstellt (Details in Task 1).
- Code-Kommentare Deutsch; `docs/` Englisch; Frontend-Copy Englisch (wie die Nachbarn).
- Consumer-Abo-Pooling ist bewusstes ToS-Risiko (Account-Sharing) — Opt-in + ToS-Hinweis sind Pflichtbestandteil (Task 2/5/6), nicht optional.
- TDD: erst roter Test, dann Implementierung; ein Commit pro Task. Vor jedem Commit die Full-Suite (`dotnet test Naudit.slnx`) einmal laufen lassen.
- **Basis ist `feat/author-sessions` (PR #53)**, nicht `main` — dieser Branch ist darauf gestapelt. Alle wiederverwendeten Typen existieren nur dort.

---

### Task 1: `AccountEntity.ShareSessionInPool` + provider-neutrale Migration

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs` (Klasse `AccountEntity`)
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260715120000_AddSharePoolFlag.cs`
- Create: `src/Naudit.Infrastructure/Data/Migrations/20260715120000_AddSharePoolFlag.Designer.cs`
- Modify: `src/Naudit.Infrastructure/Data/Migrations/NauditDbContextModelSnapshot.cs`
- Test: `tests/Naudit.Tests/NauditDbContextTests.cs`

**Interfaces:**
- Produces: `AccountEntity.ShareSessionInPool` (`bool`, Default `false`) — Task 2 liest/schreibt es, Task 3 filtert den Pool danach.

- [ ] **Step 1: Failing Test**

In `tests/Naudit.Tests/NauditDbContextTests.cs` ergänzen (nutzt das bestehende `CreateDb()`):

```csharp
    [Fact]
    public async Task ShareSessionInPool_defaultsFalse_andRoundtrips()
    {
        await using var db = CreateDb();
        var a = new AccountEntity { Username = "u", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Accounts.Add(a);
        await db.SaveChangesAsync();
        Assert.False((await db.Accounts.SingleAsync()).ShareSessionInPool); // Default false

        a.ShareSessionInPool = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.True((await db.Accounts.AsNoTracking().SingleAsync()).ShareSessionInPool);
    }
```

- [ ] **Step 2: Test läuft — rot**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "NauditDbContextTests.ShareSessionInPool_defaultsFalse_andRoundtrips"`
Expected: FAIL — `ShareSessionInPool` existiert nicht (Compile-Fehler), bzw. nach Step 3a Spalte fehlt in der DB.

- [ ] **Step 3a: Property**

In `src/Naudit.Infrastructure/Data/Entities.cs`, in `AccountEntity` nach `GitAuthorLogin`:

```csharp
    /// <summary>Opt-in: dieses Abo darf im Round-Robin-Pool für Reviews FREMDER PRs rotieren
    /// (Naudit:Ai:SessionRouting=RoundRobin). Bewusst getrennt vom Token, der für die eigenen
    /// Reviews (Author-Modus) reicht — Pool-Nutzung ist Account-Sharing und braucht Zustimmung.</summary>
    public bool ShareSessionInPool { get; set; }
```

- [ ] **Step 3b: Migration `.cs` (handgeschrieben, neutral)**

Create `src/Naudit.Infrastructure/Data/Migrations/20260715120000_AddSharePoolFlag.cs`:

```csharp
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Naudit.Infrastructure.Data.Migrations
{
    // Wie AuthorSessions/InitialUi bewusst PROVIDER-NEUTRAL handgepflegt (kein expliziter Typ).
    /// <inheritdoc />
    public partial class AddSharePoolFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShareSessionInPool",
                table: "Accounts",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ShareSessionInPool", table: "Accounts");
        }
    }
}
```

- [ ] **Step 3c: Designer `.Designer.cs` (Copy der AuthorSessions-Designer + eine Zeile)**

Kopiere `src/Naudit.Infrastructure/Data/Migrations/20260711151631_AuthorSessions.Designer.cs` nach
`20260715120000_AddSharePoolFlag.Designer.cs` und ändere genau zwei Dinge:
1. `partial class AuthorSessions` → `partial class AddSharePoolFlag` und
   `[Migration("20260711151631_AuthorSessions")]` → `[Migration("20260715120000_AddSharePoolFlag")]`.
2. Im `Naudit.Infrastructure.Data.AccountEntity`-Block, in der alphabetisch sortierten Property-Liste **nach** `b.Property<string>("Provider")…` und **vor** `b.Property<int>("Status")…`, einfügen:

```csharp
                    b.Property<bool>("ShareSessionInPool");
```

(Der Designer ist typfrei — keine `HasColumnType`-Angabe, konsistent zum Rest der Datei.)

- [ ] **Step 3d: Snapshot (bleibt SQLite-baked)**

In `src/Naudit.Infrastructure/Data/Migrations/NauditDbContextModelSnapshot.cs`, im `AccountEntity`-Block
(alphabetisch nach `Provider`, vor `Status`), die **baked** Variante einfügen:

```csharp
                    b.Property<bool>("ShareSessionInPool")
                        .HasColumnType("INTEGER");
```

- [ ] **Step 4: Test grün + Full-Suite**

Run: `dotnet test Naudit.slnx`
Expected: alle PASS (die neue Migration erzeugt die Spalte; bestehende DB-Tests bleiben grün).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Data/ tests/Naudit.Tests/NauditDbContextTests.cs
git commit -m "feat(db): AccountEntity.ShareSessionInPool + provider-neutrale Migration"
```

---

### Task 2: Pool-Query + Opt-in-Setter (`ClaudeSessionService`) + Endpoint-Flag

**Files:**
- Modify: `src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs`
- Modify: `src/Naudit.Web/Endpoints/ClaudeSessionEndpoints.cs`
- Test: `tests/Naudit.Tests/ClaudeSessionServiceTests.cs` (bestehend, aus #53), `tests/Naudit.Tests/ClaudeSessionEndpointTests.cs` (bestehend, aus #53)

**Interfaces:**
- Consumes: `AccountEntity.ShareSessionInPool` (Task 1).
- Produces:
  - `ClaudeSessionService.GetPoolCandidatesAsync(CancellationToken)` → `Task<List<AccountEntity>>` (aktiv + Token + Opt-in, Id-sortiert) — Task 3 nutzt es.
  - `ClaudeSessionService.SetShareInPoolAsync(int accountId, bool share, CancellationToken)`.
  - `/api/me/claude-session` GET liefert `shareInPool`; PUT-Body `ClaudeSessionUpdate` += `bool? ShareInPool`.

- [ ] **Step 1: Failing Tests**

In `tests/Naudit.Tests/ClaudeSessionServiceTests.cs` (Muster wie die bestehenden Tests dort — `TestDb`, `EphemeralDataProtectionProvider`) ergänzen:

```csharp
    [Fact]
    public async Task GetPoolCandidates_onlyActiveWithTokenAndOptIn_idSorted()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());

        // aktiv + Token + Opt-in ⇒ drin
        var inPool = new AccountEntity { Username = "a", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        // Token, aber KEIN Opt-in ⇒ draußen
        var noOptIn = new AccountEntity { Username = "b", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = false };
        // Opt-in, aber KEIN Token ⇒ draußen
        var noToken = new AccountEntity { Username = "c", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        // Opt-in + Token, aber pending ⇒ draußen
        var pending = new AccountEntity { Username = "d", Provider = AccountProvider.GitHub, Status = AccountStatus.Pending, CreatedAt = DateTime.UtcNow, ShareSessionInPool = true };
        db.Context.Accounts.AddRange(inPool, noOptIn, noToken, pending);
        await db.Context.SaveChangesAsync();
        await svc.SetTokenAsync(inPool.Id, "t", "a");
        await svc.SetTokenAsync(noOptIn.Id, "t", "b");
        await svc.SetTokenAsync(pending.Id, "t", "d");

        var pool = await svc.GetPoolCandidatesAsync();

        Assert.Equal(new[] { inPool.Id }, pool.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task SetShareInPool_togglesFlag()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var a = new AccountEntity { Username = "a", Provider = AccountProvider.Local, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();

        await svc.SetShareInPoolAsync(a.Id, true);
        Assert.True((await db.Context.Accounts.FindAsync(a.Id))!.ShareSessionInPool);
        await svc.SetShareInPoolAsync(a.Id, false);
        db.Context.ChangeTracker.Clear();
        Assert.False((await db.Context.Accounts.AsNoTracking().SingleAsync(x => x.Id == a.Id)).ShareSessionInPool);
    }
```

In `tests/Naudit.Tests/ClaudeSessionEndpointTests.cs` ergänzen (Muster wie die bestehenden Endpoint-Tests dort — `WebApplicationFactory`/Test-Client mit Login):

```csharp
    [Fact]
    public async Task Put_shareInPool_isPersisted_andReturnedByGet()
    {
        // Arrange: eingeloggter aktiver Account mit Token (wie die bestehenden PUT-Tests aufsetzen).
        // Details/Helper aus der bestehenden Testklasse übernehmen (Login-Cookie + Token-PUT).
        // Act: PUT { shareInPool = true }, dann GET.
        // Assert: GET-Body enthält "shareInPool": true.
    }
```

> Hinweis für die Umsetzung: den Endpoint-Test exakt am Muster der bestehenden `ClaudeSessionEndpointTests` aufbauen (gleicher Login-/PUT-Helper); erst Token setzen, dann `PUT {"shareInPool":true}`, dann `GET` und im JSON `shareInPool==true` prüfen.

- [ ] **Step 2: Tests rot**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter "ClaudeSessionServiceTests|ClaudeSessionEndpointTests"`
Expected: FAIL (Methoden/Feld fehlen).

- [ ] **Step 3a: Service**

In `src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs` ergänzen:

```csharp
    /// <summary>Pool-Kandidaten fürs Round-Robin: aktive Konten mit Token UND Opt-in, Id-sortiert
    /// (deterministische Rotationsreihenfolge). Token wird erst im Router entschlüsselt.</summary>
    public Task<List<AccountEntity>> GetPoolCandidatesAsync(CancellationToken ct = default)
        => db.Accounts
            .Where(a => a.Status == AccountStatus.Active && a.ClaudeSessionToken != null && a.ShareSessionInPool)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

    /// <summary>Setzt das Pool-Opt-in (Token bleibt unangetastet).</summary>
    public async Task SetShareInPoolAsync(int accountId, bool share, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ShareSessionInPool = share;
        await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 3b: Endpoint**

In `src/Naudit.Web/Endpoints/ClaudeSessionEndpoints.cs`:

Record erweitern:

```csharp
    public sealed record ClaudeSessionUpdate(string? Token, string? GitAuthorLogin, bool? ShareInPool);
```

GET-Body um das Flag ergänzen (im `Results.Ok(new { … })`):

```csharp
                gitAuthorLogin = acct.GitAuthorLogin,
                shareInPool = acct.ShareSessionInPool,
```

PUT-Handler: das Opt-in orthogonal zum Token behandeln (die bestehende Token-required-Semantik bleibt für den reinen Token/Login-Fall erhalten). Handler-Rumpf ersetzen durch:

```csharp
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            // Pool-Opt-in ist unabhängig vom Token — zuerst anwenden.
            if (body.ShareInPool is bool share)
                await sessions.SetShareInPoolAsync(acct.Id, share, ctx.RequestAborted);

            if (string.IsNullOrWhiteSpace(body.Token))
            {
                if (acct.ClaudeSessionToken is null)
                {
                    // Reines Opt-in-Toggle (kein Token/Login) ist ok; sonst wie bisher „token required".
                    if (body.ShareInPool is not null && string.IsNullOrWhiteSpace(body.GitAuthorLogin))
                        return Results.NoContent();
                    return Results.BadRequest(new { error = "token required" });
                }
                await sessions.SetLoginAsync(acct.Id, body.GitAuthorLogin, ctx.RequestAborted);
                return Results.NoContent();
            }

            await sessions.SetTokenAsync(acct.Id, body.Token, body.GitAuthorLogin, ctx.RequestAborted);
            return Results.NoContent();
```

- [ ] **Step 4: Tests grün + Full-Suite**

Run: `dotnet test Naudit.slnx`
Expected: alle PASS (die bestehenden `blank+kein-Token⇒400`/`blank+stored⇒204`-Tests bleiben grün — `ShareInPool` ist dort `null`).

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs src/Naudit.Web/Endpoints/ClaudeSessionEndpoints.cs tests/Naudit.Tests/ClaudeSessionServiceTests.cs tests/Naudit.Tests/ClaudeSessionEndpointTests.cs
git commit -m "feat(sessions): Pool-Kandidaten + Opt-in-Flag (Service + /api/me/claude-session)"
```

---

### Task 3: `RoundRobinCursor` + `RoundRobinSessionRouter`

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinCursor.cs`
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinSessionRouter.cs`
- Test: `tests/Naudit.Tests/RoundRobinSessionRouterTests.cs`

**Interfaces:**
- Consumes: `ClaudeSessionService.GetPoolCandidatesAsync`/`DecryptToken` (Task 2), `SessionHealthRegistry`, `AuthorSessionsOptions`, `AiOptions`, `IChatClient`, `IProcessRunner`, `ILoggerFactory`, `FallbackChatClient`, `ClaudeCodeChatClient` (alle aus #53).
- Produces: `RoundRobinCursor` (Singleton, `int Next()`), `RoundRobinSessionRouter : IAiClientRouter` — Task 4 registriert beide in DI.

- [ ] **Step 1: Failing Tests**

`tests/Naudit.Tests/RoundRobinSessionRouterTests.cs` (neu — spiegelt das Harness von `AuthorSessionRouterTests`):

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class RoundRobinSessionRouterTests
{
    private static readonly ReviewRequest Request = new("o/r", 1, "T", "alice");

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private static async Task<int> SeedPooled(TestDb db, ClaudeSessionService svc, string login, bool optIn = true, bool withToken = true)
    {
        var a = new AccountEntity { Username = login, Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow, ShareSessionInPool = optIn };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();
        if (withToken) await svc.SetTokenAsync(a.Id, $"tok-{login}", login);
        return a.Id;
    }

    private static RoundRobinSessionRouter Router(ClaudeSessionService sessions, SessionHealthRegistry health,
        RoundRobinCursor cursor, IProcessRunner runner, IChatClient global) =>
        new(sessions, health, cursor, new AuthorSessionsOptions(),
            new AiOptions { Provider = AiProvider.Ollama, Model = "egal" }, global, runner, NullLoggerFactory.Instance);

    // Hilfsfunktion: Selection abrufen UND den Client aufrufen (damit die Attribution feststeht).
    private static async Task<int?> Pick(RoundRobinSessionRouter router, IProcessRunner _ = null!)
    {
        var sel = await router.SelectAsync(Request);
        await sel.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "diff")]);
        return sel.UsedSessionAccountId();
    }

    [Fact]
    public async Task EmptyPool_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var sel = await router.SelectAsync(Request);

        Assert.Same(global, sel.Client);
        Assert.Null(sel.UsedSessionAccountId());
    }

    [Fact]
    public async Task RotatesAcrossPooledAccounts_inIdOrder()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var id1 = await SeedPooled(db, svc, "alice");
        var id2 = await SeedPooled(db, svc, "bob");
        var id3 = await SeedPooled(db, svc, "carol");
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        var picks = new[] { await Pick(router), await Pick(router), await Pick(router), await Pick(router) };

        Assert.Equal(new int?[] { id1, id2, id3, id1 }, picks);
    }

    [Fact]
    public async Task SkipsCoolingDownAccount()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var id1 = await SeedPooled(db, svc, "alice");
        var id2 = await SeedPooled(db, svc, "bob");
        var health = new SessionHealthRegistry();
        health.MarkFailure(id1, TimeSpan.FromMinutes(30));
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, health, new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        Assert.Equal(id2, await Pick(router)); // alice auf Cooldown ⇒ nur bob im Pool
        Assert.Equal(id2, await Pick(router));
    }

    [Fact]
    public async Task ExcludesNonOptInAccount()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var optIn = await SeedPooled(db, svc, "alice", optIn: true);
        await SeedPooled(db, svc, "bob", optIn: false); // Token, aber kein Opt-in
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        Assert.Equal(optIn, await Pick(router));
        Assert.Equal(optIn, await Pick(router)); // bob nie gewählt
    }

    [Fact]
    public async Task SkipsUndecryptableToken()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var good = await SeedPooled(db, svc, "alice");
        var bad = await SeedPooled(db, svc, "bob");
        var badAcct = await db.Context.Accounts.FindAsync(bad);
        badAcct!.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar";
        await db.Context.SaveChangesAsync();
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new SessionHealthRegistry(), new RoundRobinCursor(), stub, new FakeChatClient("GLOBAL"));

        // Egal wo der Cursor startet: der undekryptierbare bob wird übersprungen, alice antwortet.
        Assert.Equal(good, await Pick(router));
        Assert.Equal(good, await Pick(router));
    }
}
```

- [ ] **Step 2: Tests rot**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter RoundRobinSessionRouterTests`
Expected: FAIL — `RoundRobinCursor`/`RoundRobinSessionRouter` fehlen.

- [ ] **Step 3a: Cursor**

`src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinCursor.cs`:

```csharp
using System.Threading;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Prozess-globaler Rotationszeiger fürs Round-Robin-Routing. Bewusst in-memory
/// (kein persistenter Zustand): nach einem Neustart rotiert die Reihenfolge einfach ab 0 weiter.</summary>
public sealed class RoundRobinCursor
{
    private int _n = -1;
    /// <summary>Nächster nicht-negativer Zählwert (erste Rückgabe 0), überlaufsicher.</summary>
    public int Next() => Interlocked.Increment(ref _n) & int.MaxValue;
}
```

- [ ] **Step 3b: Router**

`src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinSessionRouter.cs`:

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Round-Robin-Routing: rotiert die opted-in Pool-Abos (aktiv + Token) über die Reviews,
/// ignoriert den Autor. Konten auf Cooldown und undekryptierbare Token werden übersprungen;
/// ist kein Kandidat nutzbar, fällt es lautlos auf den globalen Client zurück.</summary>
public sealed class RoundRobinSessionRouter(
    ClaudeSessionService sessions,
    SessionHealthRegistry health,
    RoundRobinCursor cursor,
    AuthorSessionsOptions options,
    AiOptions aiOptions,
    IChatClient globalClient,
    IProcessRunner runner,
    ILoggerFactory loggerFactory) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var pool = await sessions.GetPoolCandidatesAsync(ct);
        // Cooldown-Konten raus; Id-Reihenfolge aus der Query bleibt erhalten.
        var eligible = pool.Where(a => !health.IsCoolingDown(a.Id)).ToList();
        if (eligible.Count == 0)
            return Global();

        // Ein Cursor-Schritt pro Review; ab dort das erste Konto mit entschlüsselbarem Token nehmen
        // (undekryptierbare überspringen, nicht global fallen).
        var start = cursor.Next() % eligible.Count;
        for (var i = 0; i < eligible.Count; i++)
        {
            var account = eligible[(start + i) % eligible.Count];
            var token = sessions.DecryptToken(account);
            if (token is null)
                continue;

            var poolClient = new ClaudeCodeChatClient(new AiOptions
            {
                Provider = AiProvider.ClaudeCode,
                Model = options.Model,
                ApiKey = token,
                TimeoutSeconds = aiOptions.TimeoutSeconds,
            }, runner);

            var accountId = account.Id;
            var fallback = new FallbackChatClient(poolClient, globalClient, accountId,
                onAuthorFailure: () => health.MarkFailure(accountId, TimeSpan.FromMinutes(options.CooldownMinutes)),
                loggerFactory.CreateLogger<FallbackChatClient>());

            return new AiClientSelection(fallback, () => fallback.AnsweredBySessionAccountId);
        }

        return Global(); // alle Kandidaten-Token undekryptierbar
    }

    private AiClientSelection Global() => new(globalClient, static () => null);
}
```

- [ ] **Step 4: Tests grün + Full-Suite**

Run: `dotnet test tests/Naudit.Tests/Naudit.Tests.csproj --filter RoundRobinSessionRouterTests` (dann `dotnet test Naudit.slnx`)
Expected: alle PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinCursor.cs src/Naudit.Infrastructure/Ai/ClaudeCode/RoundRobinSessionRouter.cs tests/Naudit.Tests/RoundRobinSessionRouterTests.cs
git commit -m "feat(sessions): RoundRobinSessionRouter — rotiert den Opt-in-Pool, Cooldown/undekryptierbar übersprungen"
```

---

### Task 4: `SessionRouting`-Enum + DI-3-Wege-Schalter + SettingsCatalog

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/AiOptions.cs` (Enum + Property)
- Modify: `src/Naudit.Infrastructure/Ai/AuthorSessionsOptions.cs` (`Enabled` entfernen)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (3-Wege-Schalter + Cursor-Registrierung)
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `tests/Naudit.Tests/AuthorSessionRouterTests.cs` (Helper: `{ Enabled = true }` entfernen) + jede weitere Fundstelle von `AuthorSessions:Enabled`/`AuthorSessionsOptions.Enabled`
- Test: `tests/Naudit.Tests/` — neue/angepasste Wiring-Tests (die #53-Author-Wiring-Tests auf `SessionRouting` umstellen + einen RoundRobin-Fall)

**Interfaces:**
- Consumes: `RoundRobinSessionRouter`/`RoundRobinCursor` (Task 3), `AuthorSessionRouter`/`SingleClientRouter` (#53).
- Produces: `enum SessionRouting { Single, Author, RoundRobin }`; `AiOptions.SessionRouting` (Default `Single`); DI registriert je Enum-Wert den passenden `IAiClientRouter`. Config-Key `Naudit:Ai:SessionRouting` ersetzt `Naudit:Ai:AuthorSessions:Enabled`.

- [ ] **Step 1: Bestandsaufnahme (welche Stellen referenzieren `Enabled`?)**

Run: `grep -rn "AuthorSessions:Enabled\|AuthorSessionsOptions\b\|\.Enabled" src tests --include=*.cs | grep -i authorsession`
Erwartung: DI-Schalter, der Router-Test-Helper, evtl. ein Wiring-Test. Alle Fundstellen in dieser Task mit umstellen.

- [ ] **Step 2: Failing Wiring-Tests**

Die bestehenden Author-Sessions-Wiring-Tests (aus #53, die `Naudit:Ai:AuthorSessions:Enabled=true` setzen und `IAiClientRouter` als `AuthorSessionRouter` erwarten) auf den Enum umstellen und einen RoundRobin-Fall ergänzen. Muster (Config über `Dictionary` in eine `IConfiguration`, dann `AddNauditInfrastructure` + `BuildServiceProvider`, `GetRequiredService<IAiClientRouter>()`):

```csharp
    [Theory]
    [InlineData("Single", typeof(SingleClientRouter))]
    [InlineData("Author", typeof(AuthorSessionRouter))]
    [InlineData("RoundRobin", typeof(RoundRobinSessionRouter))]
    public void SessionRouting_selectsMatchingRouter(string mode, Type expected)
    {
        // Minimal-Config wie in den bestehenden Wiring-Tests (Platform/Provider gesetzt), plus:
        //   ["Naudit:Ai:SessionRouting"] = mode
        // AddNauditInfrastructure(config) → BuildServiceProvider → CreateScope →
        // GetRequiredService<IAiClientRouter>() ist vom Typ `expected`.
    }
```

> Umsetzung: die konkreten Config-Keys/Helper aus der bestehenden Author-Wiring-Testklasse übernehmen; nur den Schalter-Key von `AuthorSessions:Enabled` auf `SessionRouting` wechseln und die drei Enum-Werte abdecken.

- [ ] **Step 3: Enum + Option**

In `src/Naudit.Infrastructure/Ai/AiOptions.cs` (Enum neben `AiProvider`; Property in `AiOptions`):

```csharp
/// <summary>Session-Routing pro Review: globaler Provider | autor-gebunden | Round-Robin-Pool.</summary>
public enum SessionRouting { Single, Author, RoundRobin }
```

In `AiOptions` ergänzen:

```csharp
    /// <summary>Wie der Chat-Client pro Review gewählt wird (Naudit:Ai:SessionRouting).
    /// Single = globaler Provider (Default, heutiges Verhalten); Author = autor-gebunden;
    /// RoundRobin = Opt-in-Pool rundlaufend.</summary>
    public SessionRouting SessionRouting { get; set; } = SessionRouting.Single;
```

In `src/Naudit.Infrastructure/Ai/AuthorSessionsOptions.cs` die Property `Enabled` **entfernen** (Model/CooldownMinutes bleiben).

- [ ] **Step 4: DI-Schalter**

In `src/Naudit.Infrastructure/DependencyInjection.cs`, den `if (authorSessions.Enabled) … else …`-Block ersetzen durch:

```csharp
        services.AddSingleton<RoundRobinCursor>();

        // Router-Naht: 3 Modi. Single = globaler Client (heutiges Verhalten); Author/RoundRobin
        // sind scoped (brauchen ClaudeSessionService/DbContext).
        switch (aiOptions.SessionRouting)
        {
            case SessionRouting.Author:
                services.AddScoped<IAiClientRouter, AuthorSessionRouter>();
                break;
            case SessionRouting.RoundRobin:
                services.AddScoped<IAiClientRouter, RoundRobinSessionRouter>();
                break;
            default:
                services.AddSingleton<IAiClientRouter>(sp => new SingleClientRouter(sp.GetRequiredService<IChatClient>()));
                break;
        }
```

(`SessionHealthRegistry` bleibt wie gehabt als Singleton registriert.)

- [ ] **Step 5: SettingsCatalog**

In `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` den Eintrag
`new("Naudit:Ai:AuthorSessions:Enabled", false),` **ersetzen** durch:

```csharp
        new("Naudit:Ai:SessionRouting", false),
```

(`AuthorSessions:Model`/`CooldownMinutes` bleiben.)

- [ ] **Step 6: Bestandsreferenzen fixen**

`tests/Naudit.Tests/AuthorSessionRouterTests.cs`, im `Router(...)`-Helper `options ?? new AuthorSessionsOptions { Enabled = true }` → `options ?? new AuthorSessionsOptions()`. Alle weiteren in Step 1 gefundenen `Enabled`-Fundstellen analog auf `SessionRouting` umstellen.

- [ ] **Step 7: Full-Suite grün**

Run: `dotnet test Naudit.slnx`
Expected: alle PASS (inkl. der umgestellten Wiring-Tests + der drei Enum-Fälle).

- [ ] **Step 8: Commit**

```bash
git add src/Naudit.Infrastructure/ tests/Naudit.Tests/
git commit -m "feat(sessions): Naudit:Ai:SessionRouting-Enum (Single|Author|RoundRobin) + DI-Schalter"
```

---

### Task 5: Frontend — Modus-Dropdown (AiCategory) + Opt-in-Toggle (ClaudeSessionCard)

**Files:**
- Modify: `src/frontend/src/components/settings/categories/AiCategory.tsx`
- Modify: `src/frontend/src/components/ClaudeSessionCard.tsx`
- Modify: `src/frontend/src/api/types.ts` (`ClaudeSessionDto` += `shareInPool`)
- Modify: `src/frontend/src/hooks/queries.ts` (`useSaveClaudeSession`-Body += `shareInPool`)

**Interfaces:**
- Consumes: Config-Key `Naudit:Ai:SessionRouting` (Task 4), `/api/me/claude-session`-Feld `shareInPool` (Task 2).
- Produces: nur UI.

- [ ] **Step 1: AiCategory — Toggle → 3-Wege-Auswahl**

In `src/frontend/src/components/settings/categories/AiCategory.tsx`:

`const authorSessionsEnabled = …` ersetzen durch:

```tsx
  const routing = ctx.get("Naudit:Ai:SessionRouting") || "Single";
  const sessionsOn = routing === "Author" || routing === "RoundRobin";
```

Das ganze `<Panel title="Author sessions" …>` durch dieses ersetzen:

```tsx
      <Panel title="Session routing" extra="who pays for each review">
        <div className="flex flex-col gap-4 px-5 py-4">
          <Field label="Routing mode" hint="Single = the provider above. Author = the PR author's own Claude subscription. Round-robin = rotate opted-in subscriptions across reviews.">
            <select className={inputCls} value={routing} disabled={ctx.locked("Naudit:Ai:SessionRouting")}
              onChange={(e) => ctx.set("Naudit:Ai:SessionRouting", e.target.value)}>
              <option value="Single">Single — global provider</option>
              <option value="Author">Author — bring your own subscription</option>
              <option value="RoundRobin">Round-robin — rotate the opt-in pool</option>
            </select>
          </Field>
          {routing === "RoundRobin" && (
            <div className="rounded-lg border border-warn/40 bg-warn/10 px-4 py-3 text-[12.5px] leading-relaxed text-ink2">
              <b className="text-ink">Terms-of-service warning.</b> Round-robin uses one user&apos;s Claude
              subscription to review another user&apos;s PR — that is account sharing under Anthropic&apos;s
              consumer terms and can get the pooled accounts suspended. Only accounts that explicitly opt in on
              their profile take part.
            </div>
          )}
          {sessionsOn && (
            <>
              <Field label="Model" hint="CLI model alias for session runs — defaults to sonnet.">
                <input className={inputCls} value={ctx.get("Naudit:Ai:AuthorSessions:Model")} placeholder="sonnet"
                  disabled={ctx.locked("Naudit:Ai:AuthorSessions:Model")}
                  onChange={(e) => ctx.set("Naudit:Ai:AuthorSessions:Model", e.target.value)} />
              </Field>
              <Field label="Cooldown (minutes)" hint="How long a failing session is skipped — defaults to 30.">
                <input className={inputCls} value={ctx.get("Naudit:Ai:AuthorSessions:CooldownMinutes")} placeholder="30"
                  disabled={ctx.locked("Naudit:Ai:AuthorSessions:CooldownMinutes")}
                  onChange={(e) => ctx.set("Naudit:Ai:AuthorSessions:CooldownMinutes", e.target.value)} />
              </Field>
            </>
          )}
        </div>
      </Panel>
```

`Toggle` wird hier nicht mehr gebraucht — falls der Import dadurch ungenutzt ist, aus der `import { SelectableCard, Toggle }`-Zeile entfernen (Lint bricht sonst).

- [ ] **Step 2: types.ts + save-Hook**

In `src/frontend/src/api/types.ts`, `ClaudeSessionDto` um das Feld erweitern:

```ts
  shareInPool: boolean;
```

In `src/frontend/src/hooks/queries.ts`, `useSaveClaudeSession` den Body-Typ erweitern:

```ts
    mutationFn: (body: { token?: string; gitAuthorLogin?: string; shareInPool?: boolean }) =>
```

- [ ] **Step 3: ClaudeSessionCard — Opt-in-Toggle**

In `src/frontend/src/components/ClaudeSessionCard.tsx`: nach der Login-`<input>`-Zeile (vor dem Button-`<div>`) ein Opt-in-Checkbox-Block einfügen; er wird beim Save mitgeschickt.

State ergänzen (bei den anderen `useState`):

```tsx
  const [shareInPool, setShareInPool] = useState<boolean | null>(null);
  const shareValue = shareInPool ?? data.shareInPool;
```

Block vor den Buttons:

```tsx
        <label className="flex items-start gap-2 text-[12.5px] leading-relaxed text-ink2">
          <input type="checkbox" className="mt-0.5" checked={shareValue}
            onChange={(e) => setShareInPool(e.target.checked)} />
          <span>
            <b className="text-ink">Add my session to the round-robin pool.</b> Lets Naudit use my subscription
            to review <b>other</b> users&apos; PRs when round-robin routing is on. This is account sharing under
            Anthropic&apos;s consumer terms and can get my account suspended — leave off unless your team agreed to it.
          </span>
        </label>
```

Im Save-`onClick` den Body um `shareInPool` erweitern:

```tsx
                { token: token || undefined, gitAuthorLogin: loginValue || undefined, shareInPool: shareValue },
```

- [ ] **Step 4: Lint + Build**

Run: `cd src/frontend && npm run lint && npm run build && cd ../..`
Expected: Lint sauber, Build grün.

- [ ] **Step 5: Commit**

```bash
git add src/frontend/
git commit -m "feat(webui): Session-Routing-Dropdown (3 Modi) + Pool-Opt-in mit ToS-Warnung"
```

---

### Task 6: Doku

**Files:**
- Modify: `docs/author-sessions.md`
- Modify: `docs/configuration.md`
- Modify: `CLAUDE.md`

**Interfaces:** keine — reine Doku (Englisch).

- [ ] **Step 1: author-sessions.md — Round-Robin-Abschnitt**

Am Ende von `docs/author-sessions.md` einen Abschnitt ergänzen:

```markdown
## Round-robin routing (shared pool)

`Naudit:Ai:SessionRouting` selects how the chat client is chosen per review:

- `Single` (default) — the global provider (`Naudit:Ai:*`), today's behaviour.
- `Author` — the PR author's own subscription (the author-session flow above).
- `RoundRobin` — rotate the **opt-in pool** of subscriptions across reviews, ignoring
  authorship. Reviews are processed sequentially, so this spreads usage over successive
  reviews (it does **not** run reviews in parallel).

**⚠️ Terms-of-service risk.** Round-robin uses one user's Claude subscription to review
another user's PR. Under Anthropic's consumer (Pro/Max) terms this is **account sharing**
and can get the pooled accounts suspended. It is opt-in on two levels: the operator sets
`SessionRouting=RoundRobin`, and each user must explicitly enable **"Add my session to the
round-robin pool"** on their profile (a token set for author-mode is *not* pooled without
that consent). Only active accounts with a token **and** that opt-in are rotated; accounts
on cooldown are skipped, and an empty pool falls back to the global provider. Failures fall
back to the global client with one retry, exactly as in author mode.
```

- [ ] **Step 2: configuration.md — Key-Zeile**

In der Keys-Tabelle die Zeile `Naudit:Ai:AuthorSessions:Enabled` **ersetzen** durch:

```markdown
| `Naudit:Ai:SessionRouting` | `Single` (default) — global provider \| `Author` — the PR author's own Claude subscription \| `RoundRobin` — rotate the opt-in pool of subscriptions across reviews. `RoundRobin` is **account sharing** under Anthropic's consumer terms (see [author sessions](author-sessions.md)) |
```

(Falls `Naudit:Ai:AuthorSessions:Enabled` in dieser Datei nicht als eigene Zeile existiert, die neue `SessionRouting`-Zeile direkt vor `Naudit:Ai:AuthorSessions:Model` einfügen.)

- [ ] **Step 3: CLAUDE.md — Author-Sessions-Absatz erweitern**

Im „Author sessions"-Aufzählungspunkt den Toggle-Satz aktualisieren und den Modus ergänzen.
Den Satz „Toggle `Naudit:Ai:AuthorSessions:Enabled` (default `false` = today's behaviour)."
ersetzen durch:

```markdown
Mode `Naudit:Ai:SessionRouting` (`Single` default = today's behaviour | `Author` | `RoundRobin`).
`RoundRobinSessionRouter` (third impl) rotates an **opt-in pool** (`AccountEntity.ShareSessionInPool`,
active + token + opt-in) across reviews via an in-memory `RoundRobinCursor`, skipping cooling-down and
undecryptable-token accounts, empty pool ⇒ global — sequential, not parallel; it is deliberate
account-sharing under Anthropic's consumer terms, gated behind per-user consent. See `docs/author-sessions.md`.
```

- [ ] **Step 4: Commit**

```bash
git add docs/author-sessions.md docs/configuration.md CLAUDE.md
git commit -m "docs: Round-Robin-Session-Routing + ToS-Risiko dokumentieren"
```

---

## Abschluss

Nach Task 6: Full-Suite + Frontend erneut komplett (`dotnet test Naudit.slnx`, `npm run lint && npm run build`), Branch `feat/round-robin-sessions` pushen, **gestapelter PR gegen `feat/author-sessions`** (bzw. gegen `main`, sobald #53 gemergt ist — dann vorher `main` reinmergen/rebasen). Benedikt merged selbst.
