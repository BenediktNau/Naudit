# Autor-gebundene Claude-Sessions — Implementierungsplan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nutzer hinterlegen ihren Claude-Code-OAuth-Token im Profil; Reviews ihrer eigenen MRs laufen über ihre eigene Pro/Max-Session, alles andere über den globalen Provider (Fallback + Retry + Cooldown).

**Architecture:** Neue Core-Naht `IAiClientRouter` ersetzt die direkte `IChatClient`-Injektion in `ReviewService`. Feature aus (Default) ⇒ `SingleClientRouter` = heutiges Verhalten. Feature an ⇒ `AuthorSessionRouter` löst den MR-Autor auf (`ReviewRequest.AuthorLogin`, GitLab per API-Call), findet den aktiven Account mit DP-verschlüsseltem Token und baut einen per-Review-`ClaudeCodeChatClient` im `FallbackChatClient`-Wrapper (Fehler ⇒ Cooldown + ein globaler Retry). Attribution landet als `AiSessionAccountId` im Review-Audit.

**Tech Stack:** .NET 10 Minimal API, EF Core (SQLite/Postgres, provider-neutrale Migration), ASP.NET Data Protection, MEAI `IChatClient`, React/TS/Tailwind-SPA, Docker.

**Spec:** `docs/superpowers/specs/2026-07-11-author-sessions-design.md`

## Global Constraints

- Solution-Datei ist `Naudit.slnx` — `dotnet build Naudit.slnx` / `dotnet test Naudit.slnx`. **Nie** `Naudit.sln` (MSB1009).
- Branch: `feat/author-sessions` (Spec liegt schon drauf). Ein Commit pro Task.
- Code-Kommentare **Deutsch**; alles unter `docs/` (außer `docs/superpowers/`) **Englisch**.
- Core-Regel: `Naudit.Core` referenziert nur `Microsoft.Extensions.AI.Abstractions` — kein Provider-/Plattform-SDK, kein EF.
- Feature-Default **AUS**: `Naudit:Ai:AuthorSessions:Enabled=false` ⇒ Verhalten exakt wie heute; alle Bestandstests bleiben grün.
- Migrationen provider-neutral von Hand nachziehen (keine expliziten Spaltentypen; `Sqlite:Autoincrement` **und** `Npgsql:ValueGenerationStrategy` nur wo Identity nötig; Designer ohne `HasColumnType`; Snapshot bleibt SQLite-gebacken).
- Frontend-Gate: `cd src/frontend && npm run lint && npm run build` (build = `tsc --noEmit && vite build`).
- Test-Suite-Erwartung: vor Task 1 sind alle Tests grün (`dotnet test Naudit.slnx`); nach jedem Task wieder.

---

### Task 1: Core-Naht `IAiClientRouter` + `SingleClientRouter` + ReviewService-Umbau

**Files:**
- Create: `src/Naudit.Core/Abstractions/IAiClientRouter.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs` (Ctor-Parameter 1, `ReviewAsync`, `RecordAuditAsync`)
- Modify: `src/Naudit.Core/Models/ReviewAudit.cs` (neues optionales Feld)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Default-Registrierung)
- Create: `tests/Naudit.Tests/Fakes/FakeAiClientRouter.cs`
- Modify: `tests/Naudit.Tests/ReviewServiceTests.cs`, `tests/Naudit.Tests/ReviewAuditSinkTests.cs` (CreateService-Helper)
- Test: `tests/Naudit.Tests/AiClientRouterTests.cs`

**Interfaces:**
- Consumes: bestehendes `IChatClient`, `ReviewRequest`, `ReviewAudit`.
- Produces (spätere Tasks bauen darauf):
  - `public interface IAiClientRouter { Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default); }`
  - `public sealed record AiClientSelection(IChatClient Client, Func<int?> UsedSessionAccountId);`
  - `public sealed class SingleClientRouter(IChatClient client) : IAiClientRouter`
  - `ReviewAudit` bekommt als letzten Positionsparameter `int? AiSessionAccountId = null`.

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/Fakes/FakeAiClientRouter.cs` (neu):

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;

namespace Naudit.Tests.Fakes;

// Liefert einen festen Client + feste Attribution — für ReviewService-Tests.
internal sealed class FakeAiClientRouter(IChatClient client, int? sessionAccountId = null) : IAiClientRouter
{
    public ReviewRequest? LastRequest { get; private set; }

    public Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        LastRequest = request;
        return Task.FromResult(new AiClientSelection(client, () => sessionAccountId));
    }
}
```

`tests/Naudit.Tests/AiClientRouterTests.cs` (neu):

```csharp
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AiClientRouterTests
{
    [Fact]
    public async Task SingleClientRouter_returnsGlobalClient_withoutAttribution()
    {
        var chat = new FakeChatClient("egal");
        var router = new SingleClientRouter(chat);

        var selection = await router.SelectAsync(new ReviewRequest("p", 1, "T"));

        Assert.Same(chat, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }
}
```

In `tests/Naudit.Tests/ReviewServiceTests.cs` den Helper erweitern (Router + Sink injizierbar) und einen Attribution-Test ergänzen:

```csharp
    private static ReviewService CreateService(
        Microsoft.Extensions.AI.IChatClient chat,
        Naudit.Core.Abstractions.IGitPlatform git,
        ReviewOptions options,
        IEnumerable<ISastAnalyzer>? analyzers = null,
        FakeWorkspaceProvider? workspace = null,
        IPromptRedactor? redactor = null,
        IContextCollector? contextCollector = null,
        IReviewAuditSink? auditSink = null,
        IAiClientRouter? router = null)
        => new(router ?? new SingleClientRouter(chat), git, options,
            workspace ?? new FakeWorkspaceProvider(),
            analyzers ?? Array.Empty<ISastAnalyzer>(),
            new FakeFindingReducer(),
            redactor ?? new NullPromptRedactor(),
            contextCollector ?? new FakeContextCollector(),
            auditSink ?? new FakeReviewAuditSink());

    [Fact]
    public async Task ReviewAsync_recordsSessionAccountId_fromRouter()
    {
        var chat = new FakeChatClient("""{"summary":"s","comments":[]}""");
        var git = new FakeGitPlatform([new CodeChange("a.cs", "@@ -0,0 +1,1 @@\n+x")]);
        var sink = new FakeReviewAuditSink();
        var router = new FakeAiClientRouter(chat, sessionAccountId: 7);

        await CreateService(chat, git, new ReviewOptions { SystemPrompt = "SYS" },
            auditSink: sink, router: router).ReviewAsync(Request);

        Assert.Equal(7, Assert.Single(sink.Recorded).AiSessionAccountId);
        Assert.Equal(Request, router.LastRequest);
    }
```

In `tests/Naudit.Tests/ReviewAuditSinkTests.cs` den lokalen `CreateService`-Helper anpassen:

```csharp
    private static ReviewService CreateService(IChatClient chat, IGitPlatform git, IReviewAuditSink sink) =>
        new(new SingleClientRouter(chat), git, new ReviewOptions { SystemPrompt = "s" },
            new FakeWorkspaceProvider(), [], new FakeFindingReducer(),
            new NullPromptRedactor(), new FakeContextCollector(), sink);
```

- [ ] **Step 2: Tests laufen lassen — Compile-Fehler erwartet**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~AiClientRouterTests" 2>&1 | tail -20`
Expected: Build-FAIL („IAiClientRouter/AiClientSelection/SingleClientRouter nicht gefunden“; ReviewService-Ctor passt nicht).

- [ ] **Step 3: Core-Naht implementieren**

`src/Naudit.Core/Abstractions/IAiClientRouter.cs` (neu — `SingleClientRouter` bewusst daneben, gleiches Muster wie `NullPromptRedactor` in `IPromptRedactor.cs`):

```csharp
using Microsoft.Extensions.AI;
using Naudit.Core.Models;

namespace Naudit.Core.Abstractions;

/// <summary>Wählt pro Review den Chat-Client (z. B. Autor-Session statt globalem Provider).
/// UsedSessionAccountId erst NACH dem Chat-Aufruf auswerten: bei einem Fallback-Gespann steht
/// erst dann fest, welcher Pfad tatsächlich geantwortet hat.</summary>
public interface IAiClientRouter
{
    Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>Routing-Ergebnis: der zu nutzende Client plus nachträgliche Attribution
/// (Account-Id der Autor-Session oder null = globaler Provider).</summary>
public sealed record AiClientSelection(IChatClient Client, Func<int?> UsedSessionAccountId);

/// <summary>Default ohne Autor-Sessions: immer derselbe (globale) Client — heutiges Verhalten.</summary>
public sealed class SingleClientRouter(IChatClient client) : IAiClientRouter
{
    public Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(new AiClientSelection(client, static () => null));
}
```

`src/Naudit.Core/Models/ReviewAudit.cs` — Record um den letzten Parameter erweitern:

```csharp
/// <summary>Protokoll eines gelaufenen Reviews: Verdict, Findings und Token-Verbrauch (aus MEAI Usage).
/// AiSessionAccountId: Account, dessen Autor-Session das Review getragen hat (null = globaler Provider).</summary>
public sealed record ReviewAudit(
    string ProjectId,
    int MergeRequestIid,
    string Title,
    ReviewVerdict Verdict,
    string Summary,
    IReadOnlyList<AuditFinding> Findings,
    long? InputTokens,
    long? OutputTokens,
    string? Model,
    int? AiSessionAccountId = null);
```

`src/Naudit.Core/Review/ReviewService.cs` — drei Änderungen:

1. Ctor: `IChatClient chatClient` → `IAiClientRouter aiRouter` (erster Parameter).
2. In `ReviewAsync` den Chat-Aufruf ersetzen:

```csharp
        var chatOptions = new ChatOptions { ResponseFormat = ChatResponseFormat.Json };
        // Routing pro Review: Autor-Session oder globaler Client (Feature aus ⇒ immer global).
        var selection = await aiRouter.SelectAsync(request, ct);
        var response = await selection.Client.GetResponseAsync(messages, chatOptions, ct);
```

und den Audit-Aufruf am Ende von `ReviewAsync`:

```csharp
        await RecordAuditAsync(request, verdict, summary, inline, orphans, response, selection.UsedSessionAccountId(), ct);
```

3. `RecordAuditAsync` um den Parameter erweitern:

```csharp
    private async Task RecordAuditAsync(
        ReviewRequest request, ReviewVerdict verdict, string summary,
        IReadOnlyList<InlineComment> inline, IReadOnlyList<OrphanComment> orphans,
        ChatResponse response, int? aiSessionAccountId, CancellationToken ct)
```

und im `ReviewAudit`-Konstruktor `response.ModelId` → `response.ModelId, aiSessionAccountId`.

`src/Naudit.Infrastructure/DependencyInjection.cs` — direkt nach der `IChatClient`-Registrierung in `AddNauditInfrastructure`:

```csharp
        // Router-Naht: ohne Autor-Sessions (Task 8 schaltet um) immer der globale Client.
        services.AddSingleton<IAiClientRouter>(sp => new SingleClientRouter(sp.GetRequiredService<IChatClient>()));
```

- [ ] **Step 4: Ganze Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS (alle Tests; `grep -rn "new ReviewService(" src tests` darf nur noch die zwei angepassten Test-Helper zeigen).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(core): IAiClientRouter-Naht — Chat-Client wird pro Review gewählt"
```

---

### Task 2: `ReviewRequest.AuthorLogin` + GitHub-Webhook-Mapping + `POST /review`

**Files:**
- Modify: `src/Naudit.Core/Models/ReviewRequest.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs` (`GitHubUser` neu, `User`-Property)
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs`
- Modify: `src/Naudit.Web/Program.cs` (`ReviewTriggerRequest` + Request-Bau)
- Test: `tests/Naudit.Tests/GitHubWebhookTests.cs`, `tests/Naudit.Tests/GitLabWebhookTests.cs`

**Interfaces:**
- Produces: `ReviewRequest(string ProjectId, int MergeRequestIid, string Title, string? AuthorLogin = null)` — Default `null` hält alle bestehenden Aufrufer kompilierbar. `GitHubUser { string? Login }`.

- [ ] **Step 1: Failing Tests schreiben**

In `tests/Naudit.Tests/GitHubWebhookTests.cs` ergänzen (Payload-Baustil der Datei übernehmen — dort wird `GitHubWebhookPayload` direkt konstruiert bzw. aus JSON deserialisiert; beides funktioniert):

```csharp
    [Fact]
    public void ToReviewRequest_mapsAuthorLogin_fromPullRequestUser()
    {
        var payload = new GitHubWebhookPayload
        {
            Action = "opened",
            Repository = new GitHubRepository { FullName = "owner/repo" },
            PullRequest = new GitHubPullRequest { Number = 5, Title = "T", User = new GitHubUser { Login = "Alice" } },
        };

        var request = GitHubWebhook.ToReviewRequest("pull_request", payload);

        Assert.Equal("Alice", request!.AuthorLogin);
    }

    [Fact]
    public void ToReviewRequest_missingUser_leavesAuthorLoginNull()
    {
        var payload = new GitHubWebhookPayload
        {
            Action = "opened",
            Repository = new GitHubRepository { FullName = "owner/repo" },
            PullRequest = new GitHubPullRequest { Number = 5, Title = "T" },
        };

        Assert.Null(GitHubWebhook.ToReviewRequest("pull_request", payload)!.AuthorLogin);
    }
```

In `tests/Naudit.Tests/GitLabWebhookTests.cs` ergänzen (GitLab-Payload trägt nur `author_id` — der Login bleibt hier bewusst null, Task 7 löst per API auf):

```csharp
    [Fact]
    public void ToReviewRequest_leavesAuthorLoginNull()
    {
        var payload = new GitLabWebhookPayload
        {
            ObjectKind = "merge_request",
            Project = new GitLabProject { Id = 42 },
            ObjectAttributes = new GitLabMergeRequestAttributes { Iid = 7, Title = "T", Action = "open" },
        };

        Assert.Null(GitLabWebhook.ToReviewRequest(payload)!.AuthorLogin);
    }
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~WebhookTests" 2>&1 | tail -10`
Expected: Build-FAIL (`GitHubUser`/`AuthorLogin` existieren nicht).

- [ ] **Step 3: Implementieren**

`src/Naudit.Core/Models/ReviewRequest.cs`:

```csharp
namespace Naudit.Core.Models;

/// <summary>AuthorLogin: Login des MR-/PR-Autors (GitHub: aus dem Webhook; GitLab: null,
/// wird bei Bedarf per API aufgelöst) — Basis fürs Autor-Session-Routing.</summary>
public sealed record ReviewRequest(string ProjectId, int MergeRequestIid, string Title, string? AuthorLogin = null);
```

`src/Naudit.Infrastructure/Git/GitHub/GitHubDtos.cs` — `GitHubPullRequest` erweitern und `GitHubUser` ergänzen:

```csharp
public sealed class GitHubPullRequest
{
    [JsonPropertyName("number")] public int Number { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("user")] public GitHubUser? User { get; set; }
}

public sealed class GitHubUser
{
    [JsonPropertyName("login")] public string? Login { get; set; }
}
```

`src/Naudit.Infrastructure/Git/GitHub/GitHubWebhook.cs` — Return-Zeile:

```csharp
        return new ReviewRequest(payload.Repository.FullName, payload.PullRequest.Number,
            payload.PullRequest.Title ?? "", payload.PullRequest.User?.Login);
```

`src/Naudit.Web/Program.cs` — Record am Dateiende und Request-Bau im `/review`-Endpoint:

```csharp
public sealed record ReviewTriggerRequest(string ProjectId, int MergeRequestIid, string? Title, string? AuthorLogin = null);
```

```csharp
            var request = new ReviewRequest(body.ProjectId, body.MergeRequestIid, body.Title ?? string.Empty, body.AuthorLogin);
```

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: ReviewRequest transportiert den MR-Autor (GitHub-Webhook + POST /review)"
```

---

### Task 3: DB — Entity-Spalten, Migration, `ClaudeSessionService`, Audit-Attribution

**Files:**
- Modify: `src/Naudit.Infrastructure/Data/Entities.cs`
- Modify: `src/Naudit.Infrastructure/Data/NauditDbContext.cs` (FK-Konfiguration)
- Create: Migration `AuthorSessions` (+ Designer) unter `src/Naudit.Infrastructure/Data/Migrations/`
- Create: `src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs`
- Modify: `src/Naudit.Infrastructure/Ui/EfReviewAuditSink.cs` (Attribution durchreichen)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (`AddNauditDatabase`: Service registrieren)
- Create: `tests/Naudit.Tests/Fakes/TestDb.cs`
- Test: `tests/Naudit.Tests/ClaudeSessionServiceTests.cs`, `tests/Naudit.Tests/EfReviewAuditSinkTests.cs` (ein Zusatztest)

**Interfaces:**
- Produces:
  - `AccountEntity`: `string? ClaudeSessionToken`, `DateTime? ClaudeSessionUpdatedAtUtc`, `string? GitAuthorLogin` (lowercased).
  - `ReviewEntity`: `int? AiSessionAccountId`.
  - `ClaudeSessionService` (Scoped, in `AddNauditDatabase`): `Task SetTokenAsync(int accountId, string token, string? gitAuthorLogin, CancellationToken ct = default)`, `Task SetLoginAsync(int accountId, string? gitAuthorLogin, CancellationToken ct = default)`, `Task RemoveTokenAsync(int accountId, CancellationToken ct = default)`, `Task<AccountEntity?> FindByAuthorLoginAsync(string authorLogin, CancellationToken ct = default)`, `string? DecryptToken(AccountEntity account)`, `const string ProtectorPurpose = "Naudit.AiSessions"`.
  - `TestDb` (Test-Helper): in-memory-SQLite `NauditDbContext` via `EnsureCreated`.

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/Fakes/TestDb.cs` (neu):

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Tests.Fakes;

/// <summary>In-Memory-SQLite-DbContext für Service-Tests. Die offene Verbindung hält die DB am Leben.</summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    public NauditDbContext Context { get; }

    public TestDb()
    {
        _conn.Open();
        Context = new NauditDbContext(new DbContextOptionsBuilder<NauditDbContext>().UseSqlite(_conn).Options);
        Context.Database.EnsureCreated();
    }

    public void Dispose() { Context.Dispose(); _conn.Dispose(); }
}
```

`tests/Naudit.Tests/ClaudeSessionServiceTests.cs` (neu):

```csharp
using Microsoft.AspNetCore.DataProtection;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeSessionServiceTests
{
    private static AccountEntity Account(TestDb db, string username = "alice",
        AccountProvider provider = AccountProvider.GitHub, AccountStatus status = AccountStatus.Active)
    {
        var a = new AccountEntity { Username = username, Provider = provider, Status = status, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        db.Context.SaveChanges();
        return a;
    }

    private static ClaudeSessionService Service(TestDb db) =>
        new(db.Context, new EphemeralDataProtectionProvider());

    [Fact]
    public async Task SetToken_encryptsAtRest_andRoundTrips()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);

        await svc.SetTokenAsync(acct.Id, "sk-ant-oat01-geheim", null);

        Assert.NotNull(acct.ClaudeSessionToken);
        Assert.DoesNotContain("geheim", acct.ClaudeSessionToken);      // verschlüsselt at rest
        Assert.NotNull(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("sk-ant-oat01-geheim", svc.DecryptToken(acct));   // Roundtrip
    }

    [Fact]
    public async Task SetToken_gitHubAccount_autoFillsGitAuthorLogin_lowercased()
    {
        using var db = new TestDb();
        var acct = Account(db, username: "Alice", provider: AccountProvider.GitHub);

        await Service(db).SetTokenAsync(acct.Id, "tok", null);

        Assert.Equal("alice", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task SetToken_explicitLogin_winsOverAutoFill()
    {
        using var db = new TestDb();
        var acct = Account(db);

        await Service(db).SetTokenAsync(acct.Id, "tok", "Bob-GitLab");

        Assert.Equal("bob-gitlab", acct.GitAuthorLogin);
    }

    [Fact]
    public async Task FindByAuthorLogin_matchesCaseInsensitive_onlyActiveWithToken()
    {
        using var db = new TestDb();
        var active = Account(db, "alice");
        var pending = Account(db, "bob", status: AccountStatus.Pending);
        var svc = Service(db);
        await svc.SetTokenAsync(active.Id, "tok", "alice");
        await svc.SetTokenAsync(pending.Id, "tok", "bob");

        Assert.Equal(active.Id, (await svc.FindByAuthorLoginAsync("ALICE"))!.Id);
        Assert.Null(await svc.FindByAuthorLoginAsync("bob"));       // pending zählt nicht
        Assert.Null(await svc.FindByAuthorLoginAsync("unbekannt"));
    }

    [Fact]
    public async Task RemoveToken_clearsTokenAndTimestamp_keepsLogin()
    {
        using var db = new TestDb();
        var acct = Account(db);
        var svc = Service(db);
        await svc.SetTokenAsync(acct.Id, "tok", "alice");

        await svc.RemoveTokenAsync(acct.Id);

        Assert.Null(acct.ClaudeSessionToken);
        Assert.Null(acct.ClaudeSessionUpdatedAtUtc);
        Assert.Equal("alice", acct.GitAuthorLogin);   // Login bleibt — Nutzer will nur den Token weg
    }

    [Fact]
    public void DecryptToken_undecryptable_returnsNull()
    {
        using var db = new TestDb();
        var acct = Account(db);
        acct.ClaudeSessionToken = "CfDJ8-kaputt-nicht-entschluesselbar";
        db.Context.SaveChanges();

        // Keyring weg / fremder Ciphertext ⇒ null statt Crash (Semantik wie DbSettingsLoader).
        Assert.Null(Service(db).DecryptToken(acct));
    }
}
```

Zusatztest in `tests/Naudit.Tests/EfReviewAuditSinkTests.cs` (bestehende Datei; `TestDb` + Logger-Muster der Datei verwenden — dort wird ein `EfReviewAuditSink` mit DbContext + `NullLogger` o. ä. gebaut; an den vorhandenen Konstruktions-Helper anlehnen):

```csharp
    [Fact]
    public async Task Record_persistsAiSessionAccountId()
    {
        using var db = new TestDb();
        var acct = new AccountEntity { Username = "alice", Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(acct);
        await db.Context.SaveChangesAsync();
        var sink = new EfReviewAuditSink(db.Context, NullLogger<EfReviewAuditSink>.Instance);

        await sink.RecordAsync(new ReviewAudit("o/r", 1, "T", ReviewVerdict.Approve, "S", [], 1, 1, "m",
            AiSessionAccountId: acct.Id));

        Assert.Equal(acct.Id, db.Context.Reviews.Single().AiSessionAccountId);
    }
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeSessionServiceTests" 2>&1 | tail -10`
Expected: Build-FAIL (Spalten/Service existieren nicht).

- [ ] **Step 3: Entities + DbContext + Service + Sink implementieren**

`src/Naudit.Infrastructure/Data/Entities.cs` — in `AccountEntity` ergänzen:

```csharp
    /// <summary>Claude-Code-OAuth-Token für Autor-Sessions — DP-verschlüsselt (Purpose "Naudit.AiSessions"),
    /// write-only: der Klartext verlässt den Server nie wieder.</summary>
    public string? ClaudeSessionToken { get; set; }
    public DateTime? ClaudeSessionUpdatedAtUtc { get; set; }
    /// <summary>Login auf der aktiven Git-Plattform (lowercased) — matcht den MR-Autor aufs Konto.</summary>
    public string? GitAuthorLogin { get; set; }
```

in `ReviewEntity` ergänzen:

```csharp
    /// <summary>Account, dessen Autor-Session dieses Review getragen hat (null = globaler Provider).</summary>
    public int? AiSessionAccountId { get; set; }
```

`src/Naudit.Infrastructure/Data/NauditDbContext.cs` — im `ReviewEntity`-Block von `OnModelCreating`:

```csharp
        b.Entity<ReviewEntity>(e =>
        {
            e.HasIndex(x => x.CreatedAt);                          // Dashboard-Zeitreihen
            e.HasOne(x => x.Project).WithMany(p => p.Reviews)
                .HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Cascade);
            // Attribution ohne Navigation: Account weg ⇒ Review bleibt, Zuordnung wird null.
            e.HasOne<AccountEntity>().WithMany()
                .HasForeignKey(x => x.AiSessionAccountId).OnDelete(DeleteBehavior.SetNull);
        });
```

`src/Naudit.Infrastructure/Ui/ClaudeSessionService.cs` (neu):

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Naudit.Infrastructure.Data;

namespace Naudit.Infrastructure.Ui;

/// <summary>Verwaltet den pro Account hinterlegten Claude-Code-OAuth-Token (Autor-Sessions).
/// Token liegt DP-verschlüsselt in der DB; Entschlüsselung nur unmittelbar vor dem CLI-Lauf.</summary>
public sealed class ClaudeSessionService(NauditDbContext db, IDataProtectionProvider dataProtection)
{
    public const string ProtectorPurpose = "Naudit.AiSessions";

    public async Task SetTokenAsync(int accountId, string token, string? gitAuthorLogin, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ClaudeSessionToken = dataProtection.CreateProtector(ProtectorPurpose).Protect(token);
        account.ClaudeSessionUpdatedAtUtc = DateTime.UtcNow;

        // Login-Zuordnung: explizit gesetzt gewinnt; sonst bei GitHub-Accounts der Username
        // (dort ist Username = GitHub-Login). Immer lowercased (case-insensitiver Match).
        var login = !string.IsNullOrWhiteSpace(gitAuthorLogin) ? gitAuthorLogin
            : account.GitAuthorLogin ?? (account.Provider == AccountProvider.GitHub ? account.Username : null);
        account.GitAuthorLogin = login?.Trim().ToLowerInvariant();

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Nur den Login ändern (Token unangetastet) — leer/null ⇒ No-Op.</summary>
    public async Task SetLoginAsync(int accountId, string? gitAuthorLogin, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gitAuthorLogin)) return;
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.GitAuthorLogin = gitAuthorLogin.Trim().ToLowerInvariant();
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveTokenAsync(int accountId, CancellationToken ct = default)
    {
        var account = await db.Accounts.SingleAsync(a => a.Id == accountId, ct);
        account.ClaudeSessionToken = null;
        account.ClaudeSessionUpdatedAtUtc = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Aktiver Account mit Token zum Autor-Login, oder null. Bei (fehlkonfigurierten)
    /// Duplikaten gewinnt deterministisch die kleinste Id.</summary>
    public Task<AccountEntity?> FindByAuthorLoginAsync(string authorLogin, CancellationToken ct = default)
    {
        var login = authorLogin.Trim().ToLowerInvariant();
        return db.Accounts
            .Where(a => a.GitAuthorLogin == login && a.Status == AccountStatus.Active && a.ClaudeSessionToken != null)
            .OrderBy(a => a.Id)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Nicht entschlüsselbar (Keyring weg, fremder Ciphertext) ⇒ null statt Crash —
    /// gleiche Semantik wie DbSettingsLoader bei Settings-Secrets.</summary>
    public string? DecryptToken(AccountEntity account)
    {
        if (account.ClaudeSessionToken is null) return null;
        try { return dataProtection.CreateProtector(ProtectorPurpose).Unprotect(account.ClaudeSessionToken); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }
}
```

`src/Naudit.Infrastructure/Ui/EfReviewAuditSink.cs` — im `ReviewEntity`-Initializer ergänzen:

```csharp
            Model = audit.Model,
            AiSessionAccountId = audit.AiSessionAccountId,
```

`src/Naudit.Infrastructure/DependencyInjection.cs` — in `AddNauditDatabase` (neben `AccountService`):

```csharp
        services.AddScoped<Ui.ClaudeSessionService>();
```

- [ ] **Step 4: Migration erzeugen und neutralisieren**

```bash
dotnet ef migrations add AuthorSessions --project src/Naudit.Infrastructure
# falls dotnet-ef fehlt: dotnet tool install --global dotnet-ef
```

Dann die neue Migration **von Hand provider-neutral machen** (wie `AddDataProtectionKeys`): in `2026…_AuthorSessions.cs` alle `type:`-Argumente entfernen; im zugehörigen `*.Designer.cs` alle `HasColumnType`-Aufrufe entfernen. Der `NauditDbContextModelSnapshot` bleibt SQLite-gebacken (bewusst, siehe CLAUDE.md). Erwartetes `Up()`:

```csharp
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "ClaudeSessionToken", table: "Accounts", nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "ClaudeSessionUpdatedAtUtc", table: "Accounts", nullable: true);
            migrationBuilder.AddColumn<string>(name: "GitAuthorLogin", table: "Accounts", nullable: true);
            migrationBuilder.AddColumn<int>(name: "AiSessionAccountId", table: "Reviews", nullable: true);
            migrationBuilder.CreateIndex(name: "IX_Reviews_AiSessionAccountId", table: "Reviews", column: "AiSessionAccountId");
            migrationBuilder.AddForeignKey(name: "FK_Reviews_Accounts_AiSessionAccountId", table: "Reviews",
                column: "AiSessionAccountId", principalTable: "Accounts", principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
```

und `Down()` spiegelbildlich (`DropForeignKey`, `DropIndex`, vier `DropColumn`). Oben in die Migrationsklasse denselben Neutralitäts-HINWEIS-Kommentar setzen wie in `AddDataProtectionKeys`.

- [ ] **Step 5: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS (WAF-Tests migrieren die neue Migration automatisch mit).

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(db): Autor-Session-Spalten + ClaudeSessionService (DP-verschlüsselter Token, Audit-Attribution)"
```

---

### Task 4: `AuthorSessionsOptions` + `SessionHealthRegistry` + SettingsCatalog

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/AuthorSessionsOptions.cs`
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/SessionHealthRegistry.cs`
- Modify: `src/Naudit.Infrastructure/Settings/SettingsCatalog.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Options binden, Registry registrieren)
- Test: `tests/Naudit.Tests/SessionHealthRegistryTests.cs`

**Interfaces:**
- Produces:
  - `AuthorSessionsOptions { bool Enabled = false; int CooldownMinutes = 30; string Model = "sonnet"; }` (Section `Naudit:Ai:AuthorSessions`, als Singleton registriert).
  - `SessionHealthRegistry` (Singleton): `void MarkFailure(int accountId, TimeSpan cooldown)`, `bool IsCoolingDown(int accountId)`, `DateTimeOffset? CoolingDownUntil(int accountId)`; Ctor `SessionHealthRegistry(TimeProvider? time = null)`.

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/SessionHealthRegistryTests.cs` (neu):

```csharp
using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class SessionHealthRegistryTests
{
    private sealed class TestTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void UnknownAccount_isNotCoolingDown()
    {
        var registry = new SessionHealthRegistry();
        Assert.False(registry.IsCoolingDown(1));
        Assert.Null(registry.CoolingDownUntil(1));
    }

    [Fact]
    public void MarkFailure_coolsDown_untilWindowExpires()
    {
        var time = new TestTime();
        var registry = new SessionHealthRegistry(time);

        registry.MarkFailure(1, TimeSpan.FromMinutes(30));

        Assert.True(registry.IsCoolingDown(1));
        Assert.Equal(time.Now.AddMinutes(30), registry.CoolingDownUntil(1));

        time.Now = time.Now.AddMinutes(31);
        Assert.False(registry.IsCoolingDown(1));
        Assert.Null(registry.CoolingDownUntil(1));
    }

    [Fact]
    public void MarkFailure_otherAccount_isUnaffected()
    {
        var registry = new SessionHealthRegistry(new TestTime());
        registry.MarkFailure(1, TimeSpan.FromMinutes(30));
        Assert.False(registry.IsCoolingDown(2));
    }
}
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~SessionHealthRegistryTests" 2>&1 | tail -10`
Expected: Build-FAIL.

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Ai/AuthorSessionsOptions.cs` (neu):

```csharp
namespace Naudit.Infrastructure.Ai;

/// <summary>Autor-Sessions ("bring your own subscription"): Reviews eigener MRs laufen über den
/// im Profil hinterlegten Claude-Code-Token des Autors. Section Naudit:Ai:AuthorSessions.</summary>
public sealed class AuthorSessionsOptions
{
    public bool Enabled { get; set; }

    /// <summary>So lange wird eine gescheiterte Session übersprungen (Pro/Max-Limits arbeiten
    /// in 5-h-Fenstern; 30 min ist ein pragmatischer Wiederanlauf-Takt).</summary>
    public int CooldownMinutes { get; set; } = 30;

    /// <summary>CLI-Modell(-Alias) für Autor-Läufe — bewusst getrennt von Naudit:Ai:Model,
    /// das eine nur dem globalen Provider bekannte Id sein kann (z. B. Ollama-Modellname).</summary>
    public string Model { get; set; } = "sonnet";
}
```

`src/Naudit.Infrastructure/Ai/ClaudeCode/SessionHealthRegistry.cs` (neu):

```csharp
using System.Collections.Concurrent;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>In-Memory-Cooldown gescheiterter Autor-Sessions (accountId → coolUntil). Bewusst
/// nicht persistent: nach einem Neustart kostet das schlimmstenfalls einen Fehlversuch mit
/// erneutem Fallback.</summary>
public sealed class SessionHealthRegistry(TimeProvider? time = null)
{
    private readonly ConcurrentDictionary<int, DateTimeOffset> _coolUntil = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public void MarkFailure(int accountId, TimeSpan cooldown)
        => _coolUntil[accountId] = _time.GetUtcNow() + cooldown;

    public bool IsCoolingDown(int accountId) => CoolingDownUntil(accountId) is not null;

    public DateTimeOffset? CoolingDownUntil(int accountId)
        => _coolUntil.TryGetValue(accountId, out var until) && until > _time.GetUtcNow() ? until : null;
}
```

`src/Naudit.Infrastructure/Settings/SettingsCatalog.cs` — nach `new("Naudit:Ai:ApiKey", true),` einfügen:

```csharp
        new("Naudit:Ai:AuthorSessions:Enabled", false),
        new("Naudit:Ai:AuthorSessions:CooldownMinutes", false),
        new("Naudit:Ai:AuthorSessions:Model", false),
```

`src/Naudit.Infrastructure/DependencyInjection.cs` — in `AddNauditInfrastructure`, direkt nach der `IAiClientRouter`-Registrierung aus Task 1:

```csharp
        // Autor-Sessions: Optionen + Cooldown-Registry (Registry auch bei Enabled=false harmlos —
        // die Profil-API zeigt darüber den Cooldown-Status an).
        var authorSessions = configuration.GetSection("Naudit:Ai:AuthorSessions").Get<AuthorSessionsOptions>() ?? new AuthorSessionsOptions();
        services.AddSingleton(authorSessions);
        services.AddSingleton<SessionHealthRegistry>();
```

(`using Naudit.Infrastructure.Ai.ClaudeCode;` oben ergänzen.)

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: AuthorSessions-Optionen + In-Memory-Session-Cooldown (+ Settings-Katalog)"
```

---

### Task 5: `ClaudeCodeChatClient` — per-Lauf `CLAUDE_CONFIG_DIR`

**Files:**
- Modify: `src/Naudit.Infrastructure/Ai/ClaudeCode/ClaudeCodeChatClient.cs`
- Test: `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs`

**Interfaces:**
- Consumes: `ProcessSpec.Environment` (additiv zur geerbten Umgebung).
- Produces: jeder CLI-Lauf bekommt ein frisches `CLAUDE_CONFIG_DIR` (Scratch) und weiterhin `CLAUDE_CODE_OAUTH_TOKEN` aus `AiOptions.ApiKey`, falls gesetzt.

- [ ] **Step 1: Failing Tests schreiben**

In `tests/Naudit.Tests/ClaudeCodeChatClientTests.cs` ergänzen (Helper `Envelope`/`Messages` existieren dort):

```csharp
    [Fact]
    public async Task GetResponseAsync_setsPerRunConfigDir_andForwardsToken()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(
            new AiOptions { Provider = AiProvider.ClaudeCode, Model = "sonnet", ApiKey = "tok-1" }, stub);

        await client.GetResponseAsync(Messages());

        var env = stub.LastSpec!.Environment!;
        Assert.Equal("tok-1", env["CLAUDE_CODE_OAUTH_TOKEN"]);
        Assert.False(string.IsNullOrWhiteSpace(env["CLAUDE_CONFIG_DIR"]));
    }

    [Fact]
    public async Task GetResponseAsync_usesFreshConfigDir_perRun()
    {
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("OK"), ""));
        var client = new ClaudeCodeChatClient(new AiOptions { Provider = AiProvider.ClaudeCode }, stub);

        await client.GetResponseAsync(Messages());
        await client.GetResponseAsync(Messages());

        Assert.NotEqual(stub.Specs[0].Environment!["CLAUDE_CONFIG_DIR"],
                        stub.Specs[1].Environment!["CLAUDE_CONFIG_DIR"]);
    }
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeCodeChatClientTests" 2>&1 | tail -10`
Expected: FAIL (Environment ist heute `null` ohne ApiKey bzw. enthält kein `CLAUDE_CONFIG_DIR`).

- [ ] **Step 3: Implementieren**

In `ClaudeCodeChatClient.GetResponseAsync` den Env-Block ersetzen und Cleanup ergänzen:

```csharp
        // Isolation pro Lauf: eigenes CLAUDE_CONFIG_DIR, damit parallele Läufe mit unterschiedlichen
        // Tokens (Autor-Sessions) nie CLI-State teilen. Token optional aus der Config.
        var configDir = Path.Combine(Path.GetTempPath(), "naudit-claude", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(configDir);
        var env = new Dictionary<string, string?> { ["CLAUDE_CONFIG_DIR"] = configDir };
        if (!string.IsNullOrWhiteSpace(aiOptions.ApiKey))
            env["CLAUDE_CODE_OAUTH_TOKEN"] = aiOptions.ApiKey;

        var spec = new ProcessSpec(
            FileName: "claude",
            Arguments: args,
            StdIn: user,
            Environment: env,
            WorkingDirectory: Path.GetTempPath(), // neutrales CWD: kein ambient CLAUDE.md
            Timeout: TimeSpan.FromSeconds(aiOptions.TimeoutSeconds));

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(spec, cancellationToken);
        }
        finally
        {
            // Best-Effort-Aufräumen des Scratch-Config-Dirs; ein Rest ist unkritisch (Temp).
            try { Directory.Delete(configDir, recursive: true); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
```

Falls ein Bestandstest bisher `Environment == null` (ohne ApiKey) asserted: auf „enthält `CLAUDE_CONFIG_DIR`, aber kein `CLAUDE_CODE_OAUTH_TOKEN`“ umstellen.

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(claudecode): eigenes CLAUDE_CONFIG_DIR pro CLI-Lauf (Session-Isolation)"
```

---

### Task 6: `FallbackChatClient`

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/FallbackChatClient.cs`
- Test: `tests/Naudit.Tests/FallbackChatClientTests.cs`

**Interfaces:**
- Produces: `FallbackChatClient(IChatClient author, IChatClient global, int sessionAccountId, Action onAuthorFailure, ILogger logger) : IChatClient` mit `int? AnsweredBySessionAccountId { get; }` (nach dem Aufruf: Account-Id wenn die Autor-Session geantwortet hat, sonst null).

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/FallbackChatClientTests.cs` (neu):

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class FallbackChatClientTests
{
    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Session kaputt");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private static ChatMessage[] Messages() => [new(ChatRole.User, "diff")];

    [Fact]
    public async Task AuthorSucceeds_attributesSession_andSkipsFallback()
    {
        var failures = 0;
        var client = new FallbackChatClient(new FakeChatClient("AUTOR"), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("AUTOR", response.Text);
        Assert.Equal(7, client.AnsweredBySessionAccountId);
        Assert.Equal(0, failures);
    }

    [Fact]
    public async Task AuthorFails_marksFailure_andFallsBackToGlobal()
    {
        var failures = 0;
        var client = new FallbackChatClient(new ThrowingChatClient(), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        var response = await client.GetResponseAsync(Messages());

        Assert.Equal("GLOBAL", response.Text);
        Assert.Null(client.AnsweredBySessionAccountId);
        Assert.Equal(1, failures);
    }

    [Fact]
    public async Task BothFail_throws_failClosed()
    {
        var client = new FallbackChatClient(new ThrowingChatClient(), new ThrowingChatClient(),
            sessionAccountId: 7, onAuthorFailure: () => { }, NullLogger.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetResponseAsync(Messages()));
    }

    [Fact]
    public async Task Cancellation_propagates_withoutFallback()
    {
        var failures = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = new FallbackChatClient(new ThrowingChatClient(), new FakeChatClient("GLOBAL"),
            sessionAccountId: 7, onAuthorFailure: () => failures++, NullLogger.Instance);

        // Abbruch ist kein Session-Fehler: kein Cooldown, kein Global-Lauf.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetResponseAsync(Messages(), cancellationToken: cts.Token));
        Assert.Equal(0, failures);
    }
}
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~FallbackChatClientTests" 2>&1 | tail -10`
Expected: Build-FAIL.

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Ai/ClaudeCode/FallbackChatClient.cs` (neu):

```csharp
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Autor-Session mit globalem Fallback: scheitert der Autor-Lauf (JEDE Exception —
/// bewusst keine stderr-Klassifikation), meldet onAuthorFailure den Cooldown und der globale
/// Client läuft genau einmal mit denselben Messages. Scheitert auch der ⇒ fail-closed wie heute.
/// AnsweredBySessionAccountId NACH dem Aufruf lesen (pro Review eine eigene Instanz).</summary>
public sealed class FallbackChatClient(
    IChatClient author, IChatClient global, int sessionAccountId, Action onAuthorFailure, ILogger logger) : IChatClient
{
    public int? AnsweredBySessionAccountId { get; private set; }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList(); // zweifach enumerierbar für den Fallback-Lauf
        try
        {
            var response = await author.GetResponseAsync(list, options, cancellationToken);
            AnsweredBySessionAccountId = sessionAccountId;
            return response;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            onAuthorFailure();
            logger.LogWarning(ex, "Autor-Session (Account {AccountId}) fehlgeschlagen — Fallback auf den globalen Provider.", sessionAccountId);
            AnsweredBySessionAccountId = null;
            return await global.GetResponseAsync(list, options, cancellationToken);
        }
    }

    // ReviewService nutzt nur die non-streaming Variante; dünner Wrapper wie im ClaudeCodeChatClient.
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose() { }
}
```

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: FallbackChatClient — Autor-Session mit einmaligem globalen Retry + Cooldown-Meldung"
```

---

### Task 7: `IAuthorLoginResolver` (Passthrough + GitLab-API)

**Files:**
- Create: `src/Naudit.Infrastructure/Git/IAuthorLoginResolver.cs` (Interface + Passthrough)
- Create: `src/Naudit.Infrastructure/Git/GitLab/GitLabAuthorLoginResolver.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` (`GitLabUser`, `Author` am Detail-DTO)
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Registrierung je Plattform-Branch)
- Test: `tests/Naudit.Tests/AuthorLoginResolverTests.cs`

**Interfaces:**
- Produces:
  - `public interface IAuthorLoginResolver { Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default); }`
  - `PassthroughAuthorLoginResolver` (GitHub: Login steht schon im Request).
  - `GitLabAuthorLoginResolver(HttpClient http, IGitTokenProvider tokens, ILogger<GitLabAuthorLoginResolver> logger)` — ein `GET api/v4/projects/{id}/merge_requests/{iid}`, fail-quiet ⇒ null.

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/AuthorLoginResolverTests.cs` (neu — `StubHttpMessageHandler`-Verwendung an `GitLabPlatformTests` anlehnen; der Stub asserted URL und liefert einen vorbereiteten Response-Body):

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Git.GitLab;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AuthorLoginResolverTests
{
    private static readonly ReviewRequest GitLabRequest = new("42", 7, "T");

    private sealed class FixedTokenProvider : IGitTokenProvider
    {
        public Task<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
            => Task.FromResult("glpat-test");
    }

    private static GitLabAuthorLoginResolver Resolver(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://gitlab.example.com/") },
            new FixedTokenProvider(), NullLogger<GitLabAuthorLoginResolver>.Instance);

    [Fact]
    public async Task Passthrough_returnsRequestAuthorLogin()
    {
        var resolver = new PassthroughAuthorLoginResolver();
        Assert.Equal("alice", await resolver.ResolveAsync(new ReviewRequest("o/r", 1, "T", "alice")));
        Assert.Null(await resolver.ResolveAsync(new ReviewRequest("o/r", 1, "T")));
    }

    [Fact]
    public async Task GitLab_fetchesAuthorUsername_fromMrApi()
    {
        var handler = new StubHttpMessageHandler(req =>
        {
            Assert.EndsWith("api/v4/projects/42/merge_requests/7", req.RequestUri!.AbsolutePath);
            Assert.Equal("glpat-test", req.Headers.GetValues("PRIVATE-TOKEN").Single());
            return StubHttpMessageHandler.Json("""{"author":{"username":"alice"}}""");
        });

        Assert.Equal("alice", await Resolver(handler).ResolveAsync(GitLabRequest));
    }

    [Fact]
    public async Task GitLab_requestAlreadyHasLogin_skipsHttp()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("kein HTTP erwartet"));
        Assert.Equal("bob", await Resolver(handler).ResolveAsync(new ReviewRequest("42", 7, "T", "bob")));
    }

    [Fact]
    public async Task GitLab_apiError_returnsNull_failQuiet()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        Assert.Null(await Resolver(handler).ResolveAsync(GitLabRequest));
    }
}
```

Hinweis: Signatur/Factory-Methoden von `StubHttpMessageHandler` (`Json(...)` o. ä.) beim Schreiben an `tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs` ausrichten — die Assertions und Fälle oben bleiben identisch.

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~AuthorLoginResolverTests" 2>&1 | tail -10`
Expected: Build-FAIL.

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Git/IAuthorLoginResolver.cs` (neu):

```csharp
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git;

/// <summary>Liefert den Login des MR-/PR-Autors für das Autor-Session-Routing.
/// Fail-quiet: kein Autor ermittelbar ⇒ null ⇒ Review läuft über den globalen Provider.</summary>
public interface IAuthorLoginResolver
{
    Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default);
}

/// <summary>GitHub: der Login steht schon im Request (Webhook-Mapping) — kein API-Call.</summary>
public sealed class PassthroughAuthorLoginResolver : IAuthorLoginResolver
{
    public Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
        => Task.FromResult(request.AuthorLogin);
}
```

`src/Naudit.Infrastructure/Git/GitLab/GitLabDtos.cs` — `GitLabMergeRequestDetail` erweitern und `GitLabUser` ergänzen:

```csharp
public sealed class GitLabMergeRequestDetail
{
    [JsonPropertyName("diff_refs")] public GitLabDiffRefs? DiffRefs { get; set; }
    [JsonPropertyName("author")] public GitLabUser? Author { get; set; }
}

public sealed class GitLabUser
{
    [JsonPropertyName("username")] public string? Username { get; set; }
}
```

`src/Naudit.Infrastructure/Git/GitLab/GitLabAuthorLoginResolver.cs` (neu):

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Naudit.Core.Models;

namespace Naudit.Infrastructure.Git.GitLab;

/// <summary>GitLab-Autor-Auflösung: die Webhook-Payload trägt nur author_id, deshalb EIN
/// GET auf den MR (liefert author.username). Läuft nur, wenn Autor-Sessions aktiv sind
/// und der Login nicht schon im Request steht (POST /review kann ihn mitgeben).</summary>
public sealed class GitLabAuthorLoginResolver(
    HttpClient http, IGitTokenProvider tokens, ILogger<GitLabAuthorLoginResolver> logger) : IAuthorLoginResolver
{
    public async Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(request.AuthorLogin))
            return request.AuthorLogin;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"api/v4/projects/{request.ProjectId}/merge_requests/{request.MergeRequestIid}");
            req.Headers.Add("PRIVATE-TOKEN", await tokens.ResolveTokenAsync(request.ProjectId, ct));
            using var response = await http.SendAsync(req, ct);
            response.EnsureSuccessStatusCode();
            var detail = await response.Content.ReadFromJsonAsync<GitLabMergeRequestDetail>(ct);
            return detail?.Author?.Username;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // fail-quiet: ohne Autor läuft das Review über den globalen Provider.
            logger.LogWarning(ex, "GitLab-Autor-Auflösung für {Project}!{Iid} fehlgeschlagen.",
                request.ProjectId, request.MergeRequestIid);
            return null;
        }
    }
}
```

`src/Naudit.Infrastructure/DependencyInjection.cs` — im GitHub-Branch des Plattform-`switch`:

```csharp
                services.AddSingleton<IAuthorLoginResolver>(new PassthroughAuthorLoginResolver());
```

im GitLab-Branch (`default:`):

```csharp
                // Autor-Auflösung braucht denselben Host wie die GitLab-API (eigener typed Client).
                services.AddHttpClient<IAuthorLoginResolver, GitLabAuthorLoginResolver>((sp, http) =>
                {
                    var opt = sp.GetRequiredService<IOptions<GitLabOptions>>().Value;
                    http.BaseAddress = new Uri(opt.BaseUrl.TrimEnd('/') + "/");
                });
```

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: IAuthorLoginResolver — GitHub-Passthrough + GitLab-MR-API-Lookup (fail-quiet)"
```

---

### Task 8: `AuthorSessionRouter` + DI-Umschaltung

**Files:**
- Create: `src/Naudit.Infrastructure/Ai/ClaudeCode/AuthorSessionRouter.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (Toggle statt fixem `SingleClientRouter`)
- Test: `tests/Naudit.Tests/AuthorSessionRouterTests.cs`, `tests/Naudit.Tests/AiClientRouterWiringTests.cs`

**Interfaces:**
- Consumes: `ClaudeSessionService` (Task 3), `IAuthorLoginResolver` (Task 7), `SessionHealthRegistry`/`AuthorSessionsOptions` (Task 4), `FallbackChatClient` (Task 6), `ClaudeCodeChatClient` (Task 5), `IProcessRunner`, globaler `IChatClient`, `AiOptions`.
- Produces: `AuthorSessionRouter(ClaudeSessionService sessions, IAuthorLoginResolver authorResolver, SessionHealthRegistry health, AuthorSessionsOptions options, AiOptions aiOptions, IChatClient globalClient, IProcessRunner runner, ILoggerFactory loggerFactory) : IAiClientRouter` (Scoped).

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/AuthorSessionRouterTests.cs` (neu):

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class AuthorSessionRouterTests
{
    private static readonly ReviewRequest Request = new("o/r", 1, "T", "alice");

    private sealed class FixedAuthorResolver(string? login) : IAuthorLoginResolver
    {
        public Task<string?> ResolveAsync(ReviewRequest request, CancellationToken ct = default)
            => Task.FromResult(login);
    }

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private static async Task<int> SeedAccountWithToken(TestDb db, ClaudeSessionService svc, string login = "alice")
    {
        var a = new AccountEntity { Username = login, Provider = AccountProvider.GitHub, Status = AccountStatus.Active, CreatedAt = DateTime.UtcNow };
        db.Context.Accounts.Add(a);
        await db.Context.SaveChangesAsync();
        await svc.SetTokenAsync(a.Id, "tok-123", login);
        return a.Id;
    }

    private static AuthorSessionRouter Router(ClaudeSessionService sessions, IAuthorLoginResolver resolver,
        SessionHealthRegistry health, IProcessRunner runner, Microsoft.Extensions.AI.IChatClient global,
        AuthorSessionsOptions? options = null) =>
        new(sessions, resolver, health, options ?? new AuthorSessionsOptions { Enabled = true },
            new AiOptions { Provider = AiProvider.Ollama, Model = "egal" }, global, runner, NullLoggerFactory.Instance);

    [Fact]
    public async Task NoAuthorLogin_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver(null), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException("kein CLI-Lauf erwartet")), global);

        var selection = await router.SelectAsync(Request);

        Assert.Same(global, selection.Client);
        Assert.Null(selection.UsedSessionAccountId());
    }

    [Fact]
    public async Task NoAccountWithToken_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), new SessionHealthRegistry(),
            new StubProcessRunner(_ => throw new InvalidOperationException()), global);

        Assert.Same(global, (await router.SelectAsync(Request)).Client);
    }

    [Fact]
    public async Task CoolingDown_returnsGlobalClient()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var health = new SessionHealthRegistry();
        var accountId = await SeedAccountWithToken(db, svc);
        health.MarkFailure(accountId, TimeSpan.FromMinutes(30));
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), health,
            new StubProcessRunner(_ => throw new InvalidOperationException()), global);

        Assert.Same(global, (await router.SelectAsync(Request)).Client);
    }

    [Fact]
    public async Task HappyPath_runsCliWithAuthorToken_andAttributesSession()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var accountId = await SeedAccountWithToken(db, svc);
        var stub = new StubProcessRunner(_ => new ProcessResult(0, Envelope("{\"summary\":\"ok\"}"), ""));
        var router = Router(svc, new FixedAuthorResolver("alice"), new SessionHealthRegistry(), stub, new FakeChatClient("GLOBAL"));

        var selection = await router.SelectAsync(Request);
        var response = await selection.Client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "diff")]);

        Assert.Contains("ok", response.Text);
        Assert.Equal(accountId, selection.UsedSessionAccountId());          // Autor-Session hat geantwortet
        Assert.Equal("tok-123", stub.LastSpec!.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
        var args = stub.LastSpec.Arguments.ToList();
        Assert.Equal("sonnet", args[args.IndexOf("--model") + 1]);          // AuthorSessions:Model, nicht Naudit:Ai:Model
    }

    [Fact]
    public async Task AuthorRunFails_fallsBackToGlobal_andSetsCooldown()
    {
        using var db = new TestDb();
        var svc = new ClaudeSessionService(db.Context, new EphemeralDataProtectionProvider());
        var accountId = await SeedAccountWithToken(db, svc);
        var health = new SessionHealthRegistry();
        var stub = new StubProcessRunner(_ => throw new InvalidOperationException("rate limit"));
        var global = new FakeChatClient("GLOBAL");
        var router = Router(svc, new FixedAuthorResolver("alice"), health, stub, global);

        var selection = await router.SelectAsync(Request);
        var response = await selection.Client.GetResponseAsync(
            [new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, "diff")]);

        Assert.Equal("GLOBAL", response.Text);
        Assert.Null(selection.UsedSessionAccountId());
        Assert.True(health.IsCoolingDown(accountId));
    }
}
```

`tests/Naudit.Tests/AiClientRouterWiringTests.cs` (neu — Muster `RedactionWiringTests`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Core.Abstractions;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Xunit;

namespace Naudit.Tests;

public class AiClientRouterWiringTests
{
    private static void AssertRouterType<T>(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddNauditDatabase(config);       // ClaudeSessionService (Scoped) für den AuthorSessionRouter
        services.AddNauditInfrastructure(config);
        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        Assert.IsType<T>(scope.ServiceProvider.GetRequiredService<IAiClientRouter>());
    }

    private static Dictionary<string, string?> BaseSettings() => new()
    {
        ["Naudit:Git:Platform"] = "GitLab",
        ["Naudit:GitLab:BaseUrl"] = "https://gitlab.example.com",
    };

    [Fact]
    public void Default_registersSingleClientRouter()
        => AssertRouterType<SingleClientRouter>(BaseSettings());

    [Fact]
    public void Enabled_registersAuthorSessionRouter()
    {
        var settings = BaseSettings();
        settings["Naudit:Ai:AuthorSessions:Enabled"] = "true";
        AssertRouterType<AuthorSessionRouter>(settings);
    }
}
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~AuthorSessionRouter|FullyQualifiedName~AiClientRouterWiring" 2>&1 | tail -10`
Expected: Build-FAIL (`AuthorSessionRouter` fehlt).

- [ ] **Step 3: Implementieren**

`src/Naudit.Infrastructure/Ai/ClaudeCode/AuthorSessionRouter.cs` (neu):

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Naudit.Core.Abstractions;
using Naudit.Core.Models;
using Naudit.Infrastructure.Git;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Infrastructure.Ai.ClaudeCode;

/// <summary>Autor-Session-Routing: MR-Autor → aktiver Account mit Token (ohne Cooldown) →
/// per-Review-ClaudeCodeChatClient im FallbackChatClient-Gespann. Jede Nicht-Treffer-Stufe
/// fällt lautlos auf den globalen Client zurück — das Review läuft immer.</summary>
public sealed class AuthorSessionRouter(
    ClaudeSessionService sessions,
    IAuthorLoginResolver authorResolver,
    SessionHealthRegistry health,
    AuthorSessionsOptions options,
    AiOptions aiOptions,
    IChatClient globalClient,
    IProcessRunner runner,
    ILoggerFactory loggerFactory) : IAiClientRouter
{
    public async Task<AiClientSelection> SelectAsync(ReviewRequest request, CancellationToken ct = default)
    {
        var login = await authorResolver.ResolveAsync(request, ct);
        if (string.IsNullOrWhiteSpace(login))
            return Global();

        var account = await sessions.FindByAuthorLoginAsync(login, ct);
        if (account is null || health.IsCoolingDown(account.Id))
            return Global();

        var token = sessions.DecryptToken(account);
        if (token is null)
            return Global();

        // Eigene AiOptions für den CLI-Lauf: Autor-Token + AuthorSessions-Modell; Timeout wie global.
        var authorClient = new ClaudeCodeChatClient(new AiOptions
        {
            Provider = AiProvider.ClaudeCode,
            Model = options.Model,
            ApiKey = token,
            TimeoutSeconds = aiOptions.TimeoutSeconds,
        }, runner);

        var accountId = account.Id;
        var fallback = new FallbackChatClient(authorClient, globalClient, accountId,
            onAuthorFailure: () => health.MarkFailure(accountId, TimeSpan.FromMinutes(options.CooldownMinutes)),
            loggerFactory.CreateLogger<FallbackChatClient>());

        return new AiClientSelection(fallback, () => fallback.AnsweredBySessionAccountId);
    }

    private AiClientSelection Global() => new(globalClient, static () => null);
}
```

`src/Naudit.Infrastructure/DependencyInjection.cs` — die Registrierung aus Task 1/4 zu einem Toggle zusammenziehen (die `SingleClientRouter`-Zeile aus Task 1 ersetzen; Options-Bindung aus Task 4 muss davor stehen):

```csharp
        // Router-Naht: Autor-Sessions an ⇒ scoped Router (braucht ClaudeSessionService/DbContext),
        // sonst der globale Client — exakt heutiges Verhalten.
        if (authorSessions.Enabled)
            services.AddScoped<IAiClientRouter, AuthorSessionRouter>();
        else
            services.AddSingleton<IAiClientRouter>(sp => new SingleClientRouter(sp.GetRequiredService<IChatClient>()));
```

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: AuthorSessionRouter — Reviews eigener MRs laufen über die Autor-Session (Toggle, Default aus)"
```

---

### Task 9: Profil-API `/api/me/claude-session` (GET/PUT/DELETE/test)

**Files:**
- Create: `src/Naudit.Web/Endpoints/ClaudeSessionEndpoints.cs`
- Modify: `src/Naudit.Web/Program.cs` (im Immer-gemappt-Block: `app.MapClaudeSessionEndpoints();` direkt nach `app.MapAuthEndpoints(uiConfig);`)
- Test: `tests/Naudit.Tests/ClaudeSessionEndpointTests.cs`

**Interfaces:**
- Consumes: `CurrentAccount`, `ClaudeSessionService`, `SessionHealthRegistry` (optional via `GetService` — fehlt im Setup-/Recovery-Modus), `IProcessRunner`/`AuthorSessionsOptions` (optional, nur für den Test-Lauf), `ClaudeCodeChatClient`.
- Produces: `GET` ⇒ `{ configured, updatedAtUtc, coolingDownUntil, gitAuthorLogin }`; `PUT` Body `{ token?, gitAuthorLogin? }` ⇒ 204 (Blank-Token = behalten); `DELETE` ⇒ 204; `POST /test` ⇒ 200 `{ ok, error }` (Fehlschlag ist ein Ergebnis, kein 5xx) bzw. 503 ohne Pipeline.

- [ ] **Step 1: Failing Tests schreiben**

`tests/Naudit.Tests/ClaudeSessionEndpointTests.cs` (neu — Login-Muster wie `DataEndpointTests.AdminApp`, Stub-Runner via `ConfigureTestServices`):

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure.Process;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class ClaudeSessionEndpointTests : IClassFixture<TestAppFactory>
{
    private readonly TestAppFactory _factory;
    public ClaudeSessionEndpointTests(TestAppFactory factory) => _factory = factory;

    private static string Envelope(string result)
        => JsonSerializer.Serialize(new { type = "result", subtype = "success", is_error = false, result });

    private async Task<HttpClient> LoggedInApp(Func<ProcessSpec, ProcessResult>? cliResponder = null)
    {
        var db = $"Data Source={Path.Combine(Path.GetTempPath(), $"naudit-cs-{Guid.NewGuid():N}.db")}";
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Naudit:Db:ConnectionString", db);
            b.UseSetting("Naudit:Ui:Admin:Username", "root");
            b.UseSetting("Naudit:Ui:Admin:InitialPassword", "passwort123");
            if (cliResponder is not null)
                b.ConfigureTestServices(s => s.AddSingleton<IProcessRunner>(new StubProcessRunner(cliResponder)));
        });
        var client = factory.CreateDefaultClient(new CookieContainerHandler());
        await client.PostAsJsonAsync("/auth/login", new { username = "root", password = "passwort123" });
        return client;
    }

    [Fact]
    public async Task Get_withoutLogin_returns401()
    {
        var response = await _factory.CreateClient().GetAsync("/api/me/claude-session");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutGetDelete_roundTrip_neverEchoesToken()
    {
        var client = await LoggedInApp();

        var before = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.False(before.GetProperty("configured").GetBoolean());

        var put = await client.PutAsJsonAsync("/api/me/claude-session",
            new { token = "sk-ant-oat01-geheim", gitAuthorLogin = "Alice" });
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var after = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(after.GetProperty("configured").GetBoolean());
        Assert.Equal("alice", after.GetProperty("gitAuthorLogin").GetString());
        Assert.DoesNotContain("geheim", after.ToString());        // Token verlässt den Server nie

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/me/claude-session")).StatusCode);
        var cleared = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.False(cleared.GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Put_blankTokenWithoutStoredToken_returns400()
    {
        var client = await LoggedInApp();
        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { token = "", gitAuthorLogin = "alice" });
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
    }

    [Fact]
    public async Task Put_blankToken_keepsStoredToken_updatesLogin()
    {
        var client = await LoggedInApp();
        await client.PutAsJsonAsync("/api/me/claude-session", new { token = "tok", gitAuthorLogin = "alice" });

        var put = await client.PutAsJsonAsync("/api/me/claude-session", new { token = "", gitAuthorLogin = "Bob" });

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/me/claude-session");
        Assert.True(state.GetProperty("configured").GetBoolean());   // Token blieb erhalten
        Assert.Equal("bob", state.GetProperty("gitAuthorLogin").GetString());
    }

    [Fact]
    public async Task Test_runsCliWithStoredToken_andReportsOk()
    {
        ProcessSpec? seen = null;
        var client = await LoggedInApp(spec => { seen = spec; return new ProcessResult(0, Envelope("OK"), ""); });
        await client.PutAsJsonAsync("/api/me/claude-session", new { token = "tok-77", gitAuthorLogin = "alice" });

        var result = await client.PostAsync("/api/me/claude-session/test", null);
        var body = await result.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal("tok-77", seen!.Environment!["CLAUDE_CODE_OAUTH_TOKEN"]);
    }

    [Fact]
    public async Task Test_withoutToken_returns400()
    {
        var client = await LoggedInApp(_ => new ProcessResult(0, Envelope("OK"), ""));
        var result = await client.PostAsync("/api/me/claude-session/test", null);
        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
    }
}
```

- [ ] **Step 2: Tests laufen lassen**

Run: `dotnet test Naudit.slnx --filter "FullyQualifiedName~ClaudeSessionEndpointTests" 2>&1 | tail -10`
Expected: FAIL (404 — Route existiert nicht).

- [ ] **Step 3: Implementieren**

`src/Naudit.Web/Endpoints/ClaudeSessionEndpoints.cs` (neu):

```csharp
using Microsoft.Extensions.AI;
using Naudit.Infrastructure.Ai;
using Naudit.Infrastructure.Ai.ClaudeCode;
using Naudit.Infrastructure.Data;
using Naudit.Infrastructure.Process;
using Naudit.Infrastructure.Ui;

namespace Naudit.Web.Endpoints;

/// <summary>Selbstverwaltung der Autor-Session (Claude-Code-OAuth-Token) des eingeloggten Accounts.
/// Immer gemappt: GET/PUT/DELETE brauchen nur DB + Data Protection (auch Setup-/Recovery-Modus);
/// der Test-Lauf braucht die Review-Pipeline-Dienste und degradiert sonst auf 503.</summary>
public static class ClaudeSessionEndpoints
{
    public sealed record ClaudeSessionUpdate(string? Token, string? GitAuthorLogin);

    public static void MapClaudeSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/me/claude-session").RequireAuthorization();

        group.MapGet("", async (HttpContext ctx, NauditDbContext db) =>
        {
            var acct = await CurrentAccount.GetAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            // Registry kommt aus AddNauditInfrastructure — im Setup-/Recovery-Modus nicht da ⇒ kein Cooldown-Status.
            var health = ctx.RequestServices.GetService<SessionHealthRegistry>();
            return Results.Ok(new
            {
                configured = acct.ClaudeSessionToken is not null,
                updatedAtUtc = acct.ClaudeSessionUpdatedAtUtc,
                coolingDownUntil = health?.CoolingDownUntil(acct.Id),
                gitAuthorLogin = acct.GitAuthorLogin,
            });
        });

        group.MapPut("", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions, ClaudeSessionUpdate body) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            // Blank-Semantik wie Settings-Secrets: leerer Token lässt den gespeicherten unangetastet
            // (erlaubt reines Ändern des Logins); ein Erst-PUT ohne Token ist ein Fehler.
            if (string.IsNullOrWhiteSpace(body.Token))
            {
                if (acct.ClaudeSessionToken is null)
                    return Results.BadRequest(new { error = "token required" });
                await sessions.SetLoginAsync(acct.Id, body.GitAuthorLogin, ctx.RequestAborted);
                return Results.NoContent();
            }

            await sessions.SetTokenAsync(acct.Id, body.Token, body.GitAuthorLogin, ctx.RequestAborted);
            return Results.NoContent();
        });

        group.MapDelete("", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions) =>
        {
            var acct = await CurrentAccount.GetAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();
            await sessions.RemoveTokenAsync(acct.Id, ctx.RequestAborted);
            return Results.NoContent();
        });

        group.MapPost("/test", async (HttpContext ctx, NauditDbContext db, ClaudeSessionService sessions) =>
        {
            var acct = await CurrentAccount.GetActiveAsync(ctx, db);
            if (acct is null) return Results.Unauthorized();

            var runner = ctx.RequestServices.GetService<IProcessRunner>();
            var options = ctx.RequestServices.GetService<AuthorSessionsOptions>();
            if (runner is null || options is null)
                return Results.Json(new { ok = false, error = "review pipeline not available (setup/recovery mode)" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var token = sessions.DecryptToken(acct);
            if (token is null)
                return Results.BadRequest(new { error = "no token configured" });

            try
            {
                // Mini-Lauf mit dem hinterlegten Token — gleiche Semantik wie der Test-AI-Schritt im Setup-Wizard.
                var client = new ClaudeCodeChatClient(new AiOptions
                {
                    Provider = AiProvider.ClaudeCode,
                    Model = options.Model,
                    ApiKey = token,
                    TimeoutSeconds = 60,
                }, runner);
                var response = await client.GetResponseAsync(
                    [new ChatMessage(ChatRole.System, "Antworte exakt mit: OK"), new ChatMessage(ChatRole.User, "ping")],
                    cancellationToken: ctx.RequestAborted);
                return Results.Ok(new { ok = !string.IsNullOrWhiteSpace(response.Text), error = (string?)null });
            }
            catch (Exception ex) when (!ctx.RequestAborted.IsCancellationRequested)
            {
                // Fehlschlag ist ein 200-Ergebnis: das SPA zeigt die Meldung inline an.
                return Results.Ok(new { ok = false, error = ex.Message });
            }
        });
    }
}
```

In `src/Naudit.Web/Program.cs` im Immer-gemappt-Block ergänzen:

```csharp
    app.MapAuthEndpoints(uiConfig);
    app.MapClaudeSessionEndpoints();
```

- [ ] **Step 4: Suite grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(webui): /api/me/claude-session — Token verwalten + Test-Lauf (write-only)"
```

---

### Task 10: Frontend — Profil-Karte + Settings-Felder

**Files:**
- Modify: `src/frontend/src/api/types.ts` (DTOs)
- Modify: `src/frontend/src/hooks/queries.ts` (Hooks)
- Create: `src/frontend/src/components/ClaudeSessionCard.tsx`
- Modify: `src/frontend/src/components/pages/ProfilePage.tsx` (Karte einhängen)
- Modify: `src/frontend/src/components/settings/categories/AiCategory.tsx` (Author-sessions-Panel)
- Modify: `src/frontend/src/components/settings/RawKeys.tsx` (Enum-Optionen für den Toggle-Key)

**Interfaces:**
- Consumes: `/api/me/claude-session`-API aus Task 9; `Panel`/`Pill`-UI-Bausteine; `Toggle` + `Field` in den Settings; `ctx.get/set/locked` (`SettingsCtx`).
- Produces: `ClaudeSessionDto`, `ClaudeSessionTest`, Hooks `useClaudeSession`, `useSaveClaudeSession`, `useDeleteClaudeSession`, `useTestClaudeSession`.

- [ ] **Step 1: Typen + Hooks**

`src/frontend/src/api/types.ts` — anfügen:

```ts
export type ClaudeSessionDto = {
  configured: boolean;
  updatedAtUtc: string | null;
  coolingDownUntil: string | null;
  gitAuthorLogin: string | null;
};
export type ClaudeSessionTest = { ok: boolean; error: string | null };
```

`src/frontend/src/hooks/queries.ts` — anfügen (Import der neuen Typen ergänzen):

```ts
export function useClaudeSession() {
  return useQuery({ queryKey: ["claude-session"], queryFn: () => api<ClaudeSessionDto>("/api/me/claude-session") });
}

export function useSaveClaudeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body: { token?: string; gitAuthorLogin?: string }) =>
      api<void>("/api/me/claude-session", { method: "PUT", body: JSON.stringify(body) }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["claude-session"] }),
  });
}

export function useDeleteClaudeSession() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: () => api<void>("/api/me/claude-session", { method: "DELETE" }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["claude-session"] }),
  });
}

export function useTestClaudeSession() {
  return useMutation({ mutationFn: () => api<ClaudeSessionTest>("/api/me/claude-session/test", { method: "POST" }) });
}
```

- [ ] **Step 2: Profil-Karte bauen**

`src/frontend/src/components/ClaudeSessionCard.tsx` (neu):

```tsx
import { useState } from "react";
import { useClaudeSession, useSaveClaudeSession, useDeleteClaudeSession, useTestClaudeSession } from "@/hooks/queries";
import { Panel } from "@/components/ui/Panel";
import { Pill } from "@/components/ui/Pill";

const inputCls =
  "w-full rounded-lg border border-border bg-bg px-3 py-2 font-mono text-[13px] text-ink outline-none placeholder:text-ink3 focus:border-acc";
const btnPrimary =
  "cursor-pointer rounded-lg bg-acc px-3 py-1.5 text-xs font-bold text-accink transition-colors hover:bg-acc2 disabled:opacity-50";
const btnGhost =
  "cursor-pointer rounded-lg border border-border px-3 py-1.5 text-xs font-bold text-ink transition-colors hover:border-acc disabled:opacity-50";

export function ClaudeSessionCard() {
  const { data } = useClaudeSession();
  const save = useSaveClaudeSession();
  const remove = useDeleteClaudeSession();
  const test = useTestClaudeSession();
  const [token, setToken] = useState("");
  const [login, setLogin] = useState<string | null>(null);
  if (!data) return null;

  const cooling = data.coolingDownUntil !== null && new Date(data.coolingDownUntil) > new Date();
  const loginValue = login ?? data.gitAuthorLogin ?? "";

  return (
    <Panel title="Claude session" extra={data.configured ? "configured" : "not configured"}>
      <div className="flex flex-col gap-4 px-5 py-4">
        <div className="flex items-center gap-3">
          {data.configured ? <Pill kind="ok">✓ token stored</Pill> : <Pill kind="warn">● no token</Pill>}
          {cooling && (
            <Pill kind="warn">● cooling down until {new Date(data.coolingDownUntil!).toLocaleTimeString()}</Pill>
          )}
          {data.updatedAtUtc && (
            <span className="font-mono text-[11px] text-ink3">
              since {new Date(data.updatedAtUtc).toLocaleDateString()}
            </span>
          )}
        </div>

        <p className="text-[12.5px] leading-relaxed text-ink2">
          Store the OAuth token from <code className="font-mono text-acc">claude setup-token</code> (Claude Pro/Max)
          and reviews of merge requests <b>you</b> authored run on your subscription. Your token is used{" "}
          <b>only for your own MRs</b> — never for other users&apos; work.
        </p>

        <input
          type="password"
          className={inputCls}
          placeholder={data.configured ? "•••••• (stored — leave blank to keep)" : "paste token"}
          value={token}
          onChange={(e) => setToken(e.target.value)}
        />
        <input
          className={inputCls}
          placeholder="git login (your GitLab/GitHub username)"
          value={loginValue}
          onChange={(e) => setLogin(e.target.value)}
        />

        <div className="flex items-center gap-2">
          <button
            className={btnPrimary}
            disabled={save.isPending || (!token && !data.configured)}
            onClick={() =>
              save.mutate(
                { token: token || undefined, gitAuthorLogin: loginValue || undefined },
                { onSuccess: () => setToken("") },
              )
            }
          >
            Save
          </button>
          <button className={btnGhost} disabled={!data.configured || test.isPending} onClick={() => test.mutate()}>
            {test.isPending ? "Testing…" : "Test"}
          </button>
          <button
            className={`${btnGhost} text-warn hover:border-warn`}
            disabled={!data.configured || remove.isPending}
            onClick={() => remove.mutate()}
          >
            Remove
          </button>
          {test.data &&
            (test.data.ok ? <Pill kind="ok">✓ works</Pill> : <Pill kind="warn">● {test.data.error ?? "failed"}</Pill>)}
        </div>
      </div>
    </Panel>
  );
}
```

In `src/frontend/src/components/pages/ProfilePage.tsx`: Import ergänzen und die Karte direkt nach dem GitHub-App-`Panel`-Block (bzw. vor dem Token-Chart-Grid) rendern:

```tsx
import { ClaudeSessionCard } from "@/components/ClaudeSessionCard";
// … im JSX nach dem GitHub-App-Block:
      <ClaudeSessionCard />
```

- [ ] **Step 3: Settings-Kategorie erweitern**

`src/frontend/src/components/settings/categories/AiCategory.tsx`:

Import ergänzen: `import { SelectableCard, Toggle } from "../primitives";` und im Funktionskörper:

```tsx
  const authorSessionsEnabled = ctx.get("Naudit:Ai:AuthorSessions:Enabled") === "true";
```

Nach dem schließenden `</Panel>` der Provider-Settings (vor dem Restart-Hinweis-`div`) einfügen:

```tsx
      <Panel title="Author sessions" extra="bring your own subscription">
        <div className="flex flex-col gap-4 px-5 py-4">
          <div className="flex items-center justify-between gap-4">
            <div>
              <b className="text-[13.5px]">Route reviews through the author&apos;s Claude subscription</b>
              <p className="mt-1 text-[12.5px] leading-relaxed text-ink2">
                Users store their own Claude Code token on the profile page; reviews of their own MRs then run on
                their subscription. Everything else keeps using the provider above.
              </p>
            </div>
            <Toggle
              on={authorSessionsEnabled}
              disabled={ctx.locked("Naudit:Ai:AuthorSessions:Enabled")}
              onChange={(v) => ctx.set("Naudit:Ai:AuthorSessions:Enabled", v ? "true" : "false")}
              aria-label="Enable author sessions"
            />
          </div>
          {authorSessionsEnabled && (
            <>
              <Field label="Model" hint="CLI model alias for author runs — defaults to sonnet.">
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

`src/frontend/src/components/settings/RawKeys.tsx` — in die Enum-Options-Map aufnehmen:

```ts
  "Naudit:Ai:AuthorSessions:Enabled": ["true", "false"],
```

Falls die `Toggle`-Signatur in `../primitives` abweicht (`{ on, onChange, disabled, "aria-label" }` ist Stand heute): an die tatsächliche Signatur anpassen, nicht umgekehrt.

- [ ] **Step 4: Frontend-Gate**

Run: `cd src/frontend && npm run lint && npm run build`
Expected: beide grün (keine TS-/ESLint-Fehler).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat(webui): Claude-Session-Profilkarte + Author-Sessions-Settings"
```

---

### Task 11: Dockerfile (CLI ins Runtime-Image) + Doku

**Files:**
- Modify: `Dockerfile` (Runtime-Stage)
- Modify: `deploy/coolify/Dockerfile` (auf Basis-Image reduzieren)
- Create: `docs/author-sessions.md`
- Modify: `docs/configuration.md`, `docs/claudecode-provider.md`, `docs/deployment.md`, `CLAUDE.md`

**Interfaces:** keine Code-Schnittstellen — Deployment + Doku.

- [ ] **Step 1: `claude`-CLI in die Runtime-Stage**

In `Dockerfile`, in der `runtime`-Stage nach `COPY sast/rules /opt/naudit-rules` und vor `COPY --from=build /app/publish .` einfügen (Muster übernommen aus dem bisherigen `deploy/coolify/Dockerfile` — native linux-x64-Binary mit eigener Node-Runtime, checksum-verifiziert):

```dockerfile
# Claude Code CLI: Kernfunktion fuer den ClaudeCode-Provider und Autor-Sessions (Reviews ueber
# das Abo des MR-Autors). Native linux-x64-Binary (bringt eigene Node-Runtime mit), Version via
# stable-Zeiger aufgeloest und per manifest.json-Checksum verifiziert (fail-closed bei Mismatch).
# ARG = Pin/Notausgang fuer ein kaputtes CLI-Release (--build-arg CLAUDE_CODE_VERSION=x.y.z).
ARG CLAUDE_CODE_VERSION=
ADD https://downloads.claude.ai/claude-code-releases/stable /tmp/claude-stable
RUN set -eux; \
    apt-get update; \
    apt-get install -y --no-install-recommends curl jq; \
    ver="${CLAUDE_CODE_VERSION:-$(cat /tmp/claude-stable)}"; \
    base="https://downloads.claude.ai/claude-code-releases/${ver}"; \
    sum="$(curl -fsSL "${base}/manifest.json" | jq -r '.platforms."linux-x64".checksum')"; \
    curl -fsSL -o /usr/local/bin/claude "${base}/linux-x64/claude"; \
    echo "${sum}  /usr/local/bin/claude" | sha256sum -c -; \
    chmod 755 /usr/local/bin/claude; \
    apt-get purge -y curl jq; \
    apt-get autoremove -y; \
    rm -rf /var/lib/apt/lists/* /tmp/claude-stable

# CLI-State braucht ein schreibbares HOME (non-root "app", 1654); Auto-Updater aus —
# wuerde als non-root nach /usr/local/bin schreiben wollen und scheitern.
ENV HOME=/home/app \
    DISABLE_AUTOUPDATER=1
```

`deploy/coolify/Dockerfile` komplett ersetzen durch:

```dockerfile
# syntax=docker/dockerfile:1

# Seit den Autor-Sessions bringt das Haupt-Image die `claude` CLI selbst mit — dieses
# abgeleitete Image bleibt nur als stabiler Coolify-Build-Punkt erhalten (Basis + nichts).
FROM ghcr.io/benediktnau/naudit:latest
```

- [ ] **Step 2: Docker-Build verifizieren (wenn Docker verfügbar; sonst übernimmt der CI-PR-Gate)**

Run: `docker build -t naudit-authorsessions-test . && docker run --rm --entrypoint claude naudit-authorsessions-test --version`
Expected: Build ok; `claude --version` druckt eine Versionsnummer.

- [ ] **Step 3: `docs/author-sessions.md` schreiben**

```markdown
# Author sessions (bring your own Claude subscription)

With author sessions enabled, each user can store the OAuth token of their own Claude
Pro/Max subscription (from `claude setup-token`) on their Naudit profile page. Reviews of
merge requests **authored by that user** then run through the Claude Code CLI with their
token instead of the globally configured AI provider. Everything else — MRs by users
without a token, bot MRs (Renovate/Dependabot), failing sessions — falls back to the
global provider. Usage is thereby distributed across the team: everyone carries the cost
of reviewing their own work.

## Why author-bound (and not a shared pool)

A round-robin pool over contributed subscriptions would mean one user's account does
another user's work — that is account sharing and violates the Anthropic consumer terms;
pooled accounts risk being suspended. The sanctioned pattern (compare Claude Code GitHub
Actions with your own `setup-token` token) is: **your own token automates your own work.**
Naudit therefore uses a stored token *only* for MRs the owning user authored — this
promise is part of the profile UI and of this document.

## Enabling

1. Settings → AI → **Author sessions** → enable (`Naudit:Ai:AuthorSessions:Enabled=true`),
   then restart via the banner.
2. Each participating user: profile page → **Claude session** → paste the token from
   `claude setup-token`, set the git login (auto-filled for GitHub accounts), **Test**.

| Key | Default | Meaning |
| --- | --- | --- |
| `Naudit:Ai:AuthorSessions:Enabled` | `false` | Master switch. |
| `Naudit:Ai:AuthorSessions:Model` | `sonnet` | CLI model (alias or full id) for author runs — independent of `Naudit:Ai:Model`. |
| `Naudit:Ai:AuthorSessions:CooldownMinutes` | `30` | How long a failing session is skipped before it is tried again. |

## Behaviour

- **Routing:** MR author → active account with matching git login and stored token.
  GitHub: the author login comes from the webhook payload. GitLab: one extra API call
  resolves `author.username`. `POST /review` accepts an optional `authorLogin` field.
- **Fallback + retry:** any failure of an author run marks the session as cooling down
  and retries **once** on the global provider; if that fails too, the review fails closed
  (as any provider error does today). No review is lost to a rate-limited subscription.
- **Attribution:** the review audit stores which account's session carried a review
  (`AiSessionAccountId`), so the dashboard can show the distribution.
- **Storage:** tokens are encrypted at rest with ASP.NET Data Protection (purpose
  `Naudit.AiSessions`) and are write-only — the API never returns them.
- **Isolation:** every CLI run gets its own `CLAUDE_CONFIG_DIR`; parallel runs with
  different tokens never share state. The cooldown registry is in-memory by design — a
  restart merely allows one extra retry.

## Requirements

The `claude` CLI ships in the container image since this feature (pinned native binary,
checksum-verified). For bare-metal hosts, install it as described in
`docs/claudecode-provider.md`.
```

- [ ] **Step 4: Bestandsdoku nachziehen**

- `docs/configuration.md`: im AI-Abschnitt eine Unterüberschrift `### Author sessions (Naudit:Ai:AuthorSessions)` mit der Drei-Zeilen-Tabelle aus `docs/author-sessions.md` und Link dorthin ergänzen.
- `docs/claudecode-provider.md`: im Abschnitt „Non-goals“ den ersten Punkt („No Dockerfile changes here…“) ersetzen durch: `- The container image now ships the CLI (pinned, checksum-verified) — see docs/author-sessions.md; on bare-metal hosts the install steps above still apply.`
- `docs/deployment.md`: Hinweis beim Coolify-/derived-Image-Abschnitt: das abgeleitete Image ist obsolet, die CLI steckt im Haupt-Image; `deploy/coolify/Dockerfile` bleibt nur als Build-Punkt.
- `CLAUDE.md`: unter „Extension points“ einen Bullet ergänzen:

```markdown
- **Author sessions (bring your own subscription):** `IAiClientRouter` (Core `Abstractions`)
  selects the chat client per review; `SingleClientRouter` (default, feature off) returns the
  global `IChatClient`, `AuthorSessionRouter` (`src/Naudit.Infrastructure/Ai/ClaudeCode/`) routes
  MRs to the author's own Claude subscription (token stored DP-encrypted per account, profile
  page/`/api/me/claude-session`), wrapped in `FallbackChatClient` (any author failure ⇒ in-memory
  cooldown + one retry on the global client). Toggle `Naudit:Ai:AuthorSessions:Enabled`
  (default `false` = today's behaviour). See `docs/author-sessions.md`.
```

- [ ] **Step 5: Suite + Frontend final grün**

Run: `dotnet test Naudit.slnx 2>&1 | tail -5 && cd src/frontend && npm run lint && npm run build`
Expected: alles grün.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat(deploy,docs): claude CLI im Runtime-Image + Author-Sessions-Doku"
```

---

## Abschluss

- Ganze Suite + Frontend-Gate laufen (siehe Task 11 Step 5).
- Manueller E2E (Dogfooding): Feature aktivieren, eigenen Token im Profil hinterlegen, Test-Button, dann einen echten MR öffnen und im Dashboard die Attribution prüfen.
- PR gegen `main` aus `feat/author-sessions`; Benedikt merged selbst.
