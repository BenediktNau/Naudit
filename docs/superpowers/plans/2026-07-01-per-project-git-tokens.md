# Per-Projekt Git-Tokens Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Implementation note (2026-07-01):** shipped with `ProjectTokens` as a **list** of
> `{ Project, Token }` (`ProjectTokenEntry`) rather than a `Dictionary<string,string>`. Reason: a
> map key of `owner/repo` would put a slash into the *name* of a container env var (Coolify/Docker
> forbid that); a list keeps `owner/repo` in the *value*. The provider builds an internal
> case-insensitive `projectId → token` map from the list. Everything else matches this plan.

**Goal:** Statt eines einzigen App-weiten Tokens kann Naudit **pro Projekt** einen eigenen (fine-grained) GitHub-/GitLab-Token verwenden. Der Token wird zur Laufzeit anhand der `ReviewRequest.ProjectId` aufgelöst — Per-Projekt-Override aus der Config, sonst Fallback auf den bisherigen globalen Token. Rein additiv: ohne konfigurierte Per-Projekt-Tokens verhält sich Naudit exakt wie heute.

**Architecture:** Neue **Infrastructure-Naht** `IGitTokenProvider` (`ResolveToken(projectId) → token`) im Seam-Muster von `IFindingReducer`/`IPromptRedactor`, aber **nur in Infrastructure** — Core sieht nie Tokens, die Core-Regel bleibt schon per Konstruktion unberührt (kein Core-Eingriff). Default-Implementierung `ConfiguredGitTokenProvider` liest eine Per-Projekt-Map + den globalen Default-Token aus der Config. Der Token wandert vom **statischen Default-Header des typed `HttpClient`** (heute in `DependencyInjection.cs` eingebrannt) in eine **Auth-Setzung pro Request** in den beiden Platform-Clients — dort ist `request.ProjectId` in jeder Methode ohnehin vorhanden. Clone-URL-Token (`GetCheckoutAsync`) kommt ebenfalls aus dem Provider. Die Naht ist so geschnitten, dass eine spätere Quelle (Vault / Key Vault / DB) nur eine zweite `IGitTokenProvider`-Implementierung + `case` ist.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI, xUnit. Reine BCL (`System.Net.Http`, `System.Net.Http.Headers`, `System.Net.Http.Json`) — **kein** externes Paket, **kein** Dockerfile-Eingriff.

## Global Constraints

- **Core-Regel:** `Naudit.Core` bleibt unangetastet. `IGitTokenProvider` lebt in `Naudit.Infrastructure.Git` (Tokens = reiner HTTP/Infrastruktur-Belang). Kein Core-Typ, kein Core-Commit.
- **Solution-Datei ist `Naudit.slnx`** (nicht `.sln`). Build: `dotnet build Naudit.slnx`. Tests: `dotnet test Naudit.slnx`.
- **TDD:** red → green, **ein Commit pro Task**.
- **Code-Kommentare auf Deutsch**; `README`/`docs/` auf Englisch.
- **Rückwärtskompatibel:** ohne `ProjectTokens`-Eintrag löst der Provider auf den globalen `Token` auf ⇒ exakt heutiges Verhalten. Leerer/whitespace Override ⇒ ebenfalls Default (fail-safe, nie ein leerer Auth-Header).
- **Secrets bleiben aus `appsettings.json` heraus:** Per-Projekt-Tokens gehören in user-secrets / Env / Deployment-Secrets, genau wie der globale `Token` (siehe `docs/configuration.md`). In `appsettings.json` steht höchstens ein leeres `ProjectTokens: {}` als Struktur-Hinweis.
- **Auth pro Request, thread-safe:** der Token wird je `HttpRequestMessage` gesetzt (nicht als `DefaultRequestHeaders` auf dem gepoolten Client) — der typed Client bleibt für Per-Projekt-Auth wiederverwendbar.

## Pre-existing test note

`SastWiringTests.Disabled_registersNoAnalyzers_butReviewServiceResolves` ist auf dieser Maschine **vor** dieser Arbeit rot (resolved `ReviewService` ⇒ baut den GitLab-`HttpClient` ⇒ `new Uri("/")` bei leerer `GitLab:BaseUrl`, `DependencyInjection.cs`). Nicht Teil dieses Plans; **nicht anfassen**. Diese Arbeit ändert an der `BaseAddress`-Zeile nichts (nur die Auth-Header-Zeile entfällt), das Verhalten bleibt gleich. Neue Wiring-Tests resolven **nur** `IGitTokenProvider` (braucht kein `IGitPlatform` / keine `BaseUrl`).

## Key-Format der Per-Projekt-Map

Der Map-Schlüssel muss exakt `ReviewRequest.ProjectId` sein, wie ihn das Webhook-Mapping liefert:
- **GitHub:** `"owner/repo"` (`GitHubWebhook.ToReviewRequest` → `Repository.FullName`). Case-insensitiv (GitHub-Namen sind case-insensitiv) ⇒ der Provider normalisiert die Map auf `StringComparer.OrdinalIgnoreCase`.
- **GitLab:** die **numerische** Projekt-ID als String (`GitLabWebhook.ToReviewRequest` → `Project.Id.ToString()`), **nicht** der Pfad. Für Ziffern ist `OrdinalIgnoreCase` harmlos.

Config-Beispiel (Doppelpunkt trennt die Hierarchie; `owner/repo` ist **ein** Segment, der Slash ist kein Separator):

```
Naudit:GitHub:ProjectTokens:octo/hello-world   = github_pat_...
Naudit:GitLab:ProjectTokens:12345              = glpat-...
```

## File Structure

**Neu (Infrastructure):**
- `src/Naudit.Infrastructure/Git/IGitTokenProvider.cs` — Interface + `ConfiguredGitTokenProvider`.

**Geändert (Infrastructure):**
- `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs` — `ProjectTokens`-Map.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabOptions.cs` — `ProjectTokens`-Map.
- `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` — Ctor `IGitTokenProvider` statt `IOptions<GitHubOptions>`, Auth pro Request, Clone-URL-Token aus Provider.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` — analog.
- `src/Naudit.Infrastructure/DependencyInjection.cs` — `IGitTokenProvider` registrieren (aktive Plattform), Default-Auth-Header aus den typed Clients entfernen.

**Neu (Tests):**
- `tests/Naudit.Tests/GitTokenProviderTests.cs` — Resolution/Fallback (unit).
- `tests/Naudit.Tests/GitTokenWiringTests.cs` — DI-Wiring von `IGitTokenProvider` (GitHub + GitLab).
- Geändert: `tests/Naudit.Tests/GitHubPlatformTests.cs`, `tests/Naudit.Tests/GitLabPlatformTests.cs` — neuer Ctor + Per-Projekt-Override-Test.

**Geändert (Docs/Config):**
- `docs/configuration.md`, `docs/platform-setup.md`, `src/Naudit.Web/appsettings.json`, `CLAUDE.md`.

---

### Task 1: `IGitTokenProvider` + `ConfiguredGitTokenProvider` + `ProjectTokens` + DI-Registrierung (TDD)

Die Naht und ihre config-basierte Default-Implementierung; wird registriert, aber noch von niemandem konsumiert (Suite bleibt grün).

**Files:**
- Create: `src/Naudit.Infrastructure/Git/IGitTokenProvider.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabOptions.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`
- Create: `tests/Naudit.Tests/GitTokenProviderTests.cs`
- Create: `tests/Naudit.Tests/GitTokenWiringTests.cs`

**Interfaces:**
- `Naudit.Infrastructure.Git.IGitTokenProvider` mit `string ResolveToken(string projectId)`.
- `ConfiguredGitTokenProvider(string defaultToken, IReadOnlyDictionary<string,string> projectTokens) : IGitTokenProvider`.
- `GitHubOptions.ProjectTokens` / `GitLabOptions.ProjectTokens` : `Dictionary<string,string> = new()`.

- [ ] **Step 1 (RED): `GitTokenProviderTests` schreiben**

```csharp
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class GitTokenProviderTests
{
    [Fact]
    public void ResolveToken_projectOverride_wins()
    {
        var p = new ConfiguredGitTokenProvider("default-tok",
            new Dictionary<string, string> { ["octo/repo"] = "proj-tok" });
        Assert.Equal("proj-tok", p.ResolveToken("octo/repo"));
    }

    [Fact]
    public void ResolveToken_unknownProject_fallsBackToDefault()
    {
        var p = new ConfiguredGitTokenProvider("default-tok", new Dictionary<string, string>());
        Assert.Equal("default-tok", p.ResolveToken("octo/other"));
    }

    [Fact]
    public void ResolveToken_blankOverride_fallsBackToDefault()
    {
        var p = new ConfiguredGitTokenProvider("default-tok",
            new Dictionary<string, string> { ["octo/repo"] = "   " });
        Assert.Equal("default-tok", p.ResolveToken("octo/repo"));
    }

    [Fact]
    public void ResolveToken_isCaseInsensitiveForOwnerRepoKeys()
    {
        var p = new ConfiguredGitTokenProvider("default-tok",
            new Dictionary<string, string> { ["Octo/Repo"] = "proj-tok" });
        Assert.Equal("proj-tok", p.ResolveToken("octo/repo"));
    }
}
```

`GitTokenWiringTests` (baut den Provider wie `SastWiringTests.Build`, resolved **nur** `IGitTokenProvider`):

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Naudit.Infrastructure;
using Naudit.Infrastructure.Git;
using Xunit;

namespace Naudit.Tests;

public class GitTokenWiringTests
{
    private static ServiceProvider Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNauditInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void GitHub_resolvesProviderFromGitHubSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitHub",
            ["Naudit:GitHub:Token"] = "default-tok",
            ["Naudit:GitHub:ProjectTokens:octo/repo"] = "proj-tok",
        });
        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", provider.ResolveToken("octo/repo"));
        Assert.Equal("default-tok", provider.ResolveToken("octo/other"));
    }

    [Fact]
    public void GitLab_resolvesProviderFromGitLabSection()
    {
        using var sp = Build(new()
        {
            ["Naudit:Git:Platform"] = "GitLab",
            ["Naudit:GitLab:Token"] = "default-tok",
            ["Naudit:GitLab:ProjectTokens:12345"] = "proj-tok",
        });
        var provider = sp.GetRequiredService<IGitTokenProvider>();
        Assert.Equal("proj-tok", provider.ResolveToken("12345"));
        Assert.Equal("default-tok", provider.ResolveToken("999"));
    }
}
```

Run: `dotnet test Naudit.slnx --filter "GitTokenProviderTests|GitTokenWiringTests"` → Expected: FAIL (Typen/Registrierung fehlen).

- [ ] **Step 2 (GREEN): Naht + Options + Registrierung**

`src/Naudit.Infrastructure/Git/IGitTokenProvider.cs`:

```csharp
namespace Naudit.Infrastructure.Git;

/// <summary>Löst den Git-API-Token pro Projekt auf. Infrastruktur-Belang — Core sieht nie Tokens.
/// Default-Implementierung liest die Config; eine spätere Quelle (Vault/DB) ist nur eine zweite Impl.</summary>
public interface IGitTokenProvider
{
    /// <summary>Per-Projekt-Override, sonst der globale Default-Token.</summary>
    string ResolveToken(string projectId);
}

/// <summary>Config-basierter Provider: Per-Projekt-Map + globaler Default. Leerer/whitespace
/// Override ⇒ Default (nie ein leerer Auth-Header). Keys case-insensitiv (GitHub owner/repo).</summary>
public sealed class ConfiguredGitTokenProvider : IGitTokenProvider
{
    private readonly string _defaultToken;
    private readonly IReadOnlyDictionary<string, string> _projectTokens;

    public ConfiguredGitTokenProvider(string defaultToken, IReadOnlyDictionary<string, string> projectTokens)
    {
        _defaultToken = defaultToken;
        // Auf OrdinalIgnoreCase normalisieren: GitHub-Namen sind case-insensitiv, GitLab-IDs rein numerisch.
        _projectTokens = new Dictionary<string, string>(projectTokens, StringComparer.OrdinalIgnoreCase);
    }

    public string ResolveToken(string projectId)
        => _projectTokens.TryGetValue(projectId, out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : _defaultToken;
}
```

`GitHubOptions` / `GitLabOptions` je um eine Property ergänzen (Kommentar auf Deutsch):

```csharp
public Dictionary<string, string> ProjectTokens { get; set; } = new();  // Per-Projekt-Override: "owner/repo" bzw. Projekt-ID → Token
```

`DependencyInjection.cs` — in **beiden** `switch`-Zweigen (GitHub/GitLab) den Provider aus der jeweils aktiven Section registrieren. Die Section wird ohnehin schon gebunden/`Configure`d; hier zusätzlich einmal `.Get<>()` für die Provider-Seeds (analog zu `aiOptions`/`sastOptions`):

```csharp
// GitHub-Zweig, direkt nach services.Configure<GitHubOptions>(...):
var gitHubOptions = configuration.GetSection("Naudit:GitHub").Get<GitHubOptions>() ?? new GitHubOptions();
services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitHubOptions.Token, gitHubOptions.ProjectTokens));

// GitLab-Zweig, direkt nach services.Configure<GitLabOptions>(...):
var gitLabOptions = configuration.GetSection("Naudit:GitLab").Get<GitLabOptions>() ?? new GitLabOptions();
services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitLabOptions.Token, gitLabOptions.ProjectTokens));
```

(Der Default-Auth-Header in den typed Clients bleibt in **diesem** Task noch stehen — er wird erst in Task 2/3 entfernt, wenn die Platform-Clients pro Request authentisieren. So bleibt jeder Task für sich grün.)

Run: `dotnet test Naudit.slnx --filter "GitTokenProviderTests|GitTokenWiringTests"` → Expected: PASS.

- [ ] **Step 3: Commit** — `feat(infra): IGitTokenProvider-Naht + ConfiguredGitTokenProvider + ProjectTokens-Config`.

---

### Task 2: GitHub — Auth pro Request aus dem Provider (TDD)

`GitHubPlatform` authentisiert jeden Request mit dem projekt-aufgelösten Token; der Default-Header entfällt.

**Files:**
- Modify: `tests/Naudit.Tests/GitHubPlatformTests.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (GitHub-Zweig: Auth-Header-Zeile entfernen)

- [ ] **Step 1 (RED): Tests umstellen + Override-Test**

In `GitHubPlatformTests`: den Ctor-Parameter `Options.Create(new GitHubOptions { Token = "tok" })` durch einen Provider ersetzen. Ein kleiner lokaler Helper hält die Bestands-Tests knapp:

```csharp
private static IGitTokenProvider Tokens(string @default = "tok", Dictionary<string, string>? map = null)
    => new ConfiguredGitTokenProvider(@default, map ?? new());
```

Alle `new GitHubPlatform(client, Options.Create(...))` → `new GitHubPlatform(client, Tokens())`.
`GetCheckoutAsync_buildsCloneUrlWithToken_andPrRef` bleibt inhaltlich gleich (Default-Token `tok` landet in der Clone-URL), nur der Ctor ändert sich.

Neuer Test — Per-Projekt-Token landet im Auth-Header **pro Request**:

```csharp
[Fact]
public async Task GetChangesAsync_usesPerProjectToken_inAuthHeader()
{
    var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("[]", Encoding.UTF8, "application/json"),
    });
    var platform = new GitHubPlatform(
        ClientReturning(HttpStatusCode.OK, "[]", capture),
        Tokens("default-tok", new() { ["octo/hello-world"] = "proj-tok" }));

    await platform.GetChangesAsync(Request);  // Request.ProjectId == "octo/hello-world"

    var auth = capture.LastRequest!.Headers.Authorization!;
    Assert.Equal("Bearer", auth.Scheme);
    Assert.Equal("proj-tok", auth.Parameter);
}
```

(Optional analog: `GetCheckoutAsync` mit gemapptem Projekt ⇒ Clone-URL enthält `x-access-token:proj-tok`.)

Run: `dotnet test Naudit.slnx --filter GitHubPlatformTests` → Expected: FAIL.

- [ ] **Step 2 (GREEN): `GitHubPlatform` umstellen**

Ctor `IGitTokenProvider tokens` statt `IOptions<GitHubOptions> options`. `using System.Net.Http.Headers;` ergänzen. Ein Helper setzt Auth je Request; die statischen Header (Accept/UA/API-Version) bleiben am typed Client:

```csharp
public sealed class GitHubPlatform(HttpClient http, IGitTokenProvider tokens) : IGitPlatform
{
    // Auth pro Request aus dem projekt-aufgelösten Token — nicht als Default-Header (Per-Projekt-fähig).
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string projectId, object? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.ResolveToken(projectId));
        if (body is not null)
            req.Content = JsonContent.Create(body);
        return await http.SendAsync(req, ct);
    }
    ...
}
```

- `GetChangesAsync`: `using var resp = await SendAsync(HttpMethod.Get, url, request.ProjectId, null, ct); resp.EnsureSuccessStatusCode(); var files = await resp.Content.ReadFromJsonAsync<List<GitHubFile>>(ct);` (Null-/leer-Verhalten wie bisher: `files is null → []`).
- `PostReviewAsync`: `var response = await SendAsync(HttpMethod.Post, url, request.ProjectId, payload, ct); response.EnsureSuccessStatusCode();`.
- `GetCheckoutAsync`: GET wie oben; Clone-URL-Token = `tokens.ResolveToken(request.ProjectId)` statt `options.Value.Token`.

`DependencyInjection.cs`, GitHub-Zweig: die Zeile
`http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", opt.Token);` **entfernen** (Accept/UA/API-Version bleiben). Der `sp.GetRequiredService<IOptions<GitHubOptions>>()`-Zugriff im Configure-Callback wird für BaseUrl weiterhin gebraucht (nur der Token-Header entfällt).

Run: `dotnet test Naudit.slnx --filter GitHubPlatformTests` → Expected: PASS. Zusätzlich `dotnet build Naudit.slnx` grün (Ctor-Wechsel wird von der typed-Client-Factory über den registrierten `IGitTokenProvider` bedient).

- [ ] **Step 3: Commit** — `feat(github): Per-Projekt-Token pro Request (Auth-Header aus IGitTokenProvider)`.

---

### Task 3: GitLab — Auth pro Request aus dem Provider (TDD)

Analog zu Task 2, aber Header `PRIVATE-TOKEN` (kein `Authorization`).

**Files:**
- Modify: `tests/Naudit.Tests/GitLabPlatformTests.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs` (GitLab-Zweig: `PRIVATE-TOKEN`-Default-Zeile entfernen)

- [ ] **Step 1 (RED): Tests umstellen + Override-Test**

`GitLabPlatformTests`: Ctor-Umstellung wie bei GitHub (`Tokens()`-Helper). Neuer Test — der projekt-aufgelöste Token steht im `PRIVATE-TOKEN`-Header **jedes** Requests (z. B. am GET von `GetChangesAsync`, `Request.ProjectId == "1"`):

```csharp
[Fact]
public async Task GetChangesAsync_usesPerProjectToken_inPrivateTokenHeader()
{
    var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"changes":[]}""", Encoding.UTF8, "application/json"),
    });
    var platform = new GitLabPlatform(
        ClientReturning(HttpStatusCode.OK, """{"changes":[]}""", capture),
        Tokens("default-tok", new() { ["1"] = "proj-tok" }));

    await platform.GetChangesAsync(Request);

    Assert.Equal("proj-tok", capture.LastRequest!.Headers.GetValues("PRIVATE-TOKEN").Single());
}
```

Run: `dotnet test Naudit.slnx --filter GitLabPlatformTests` → Expected: FAIL.

- [ ] **Step 2 (GREEN): `GitLabPlatform` umstellen**

Ctor `IGitTokenProvider tokens` statt `IOptions<GitLabOptions> options`. Helper wie bei GitHub, aber:

```csharp
req.Headers.Add("PRIVATE-TOKEN", tokens.ResolveToken(projectId));
```

- `GetChangesAsync`, `PostReviewAsync` (der GET auf `basePath` **und** jeder Notes-/Discussions-POST), `GetCheckoutAsync`-GET: alle über den Helper. Achtung: `PostReviewAsync` setzt **mehrere** Requests ab — jeder muss den Token tragen (der `StubHttpMessageHandler.Calls`-Verlauf deckt das ab).
- `GetCheckoutAsync`: Clone-URL-Token = `tokens.ResolveToken(request.ProjectId)` statt `options.Value.Token`.

`DependencyInjection.cs`, GitLab-Zweig: Zeile
`http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", opt.Token);` **entfernen** (BaseAddress bleibt).

Run: `dotnet test Naudit.slnx --filter GitLabPlatformTests` → Expected: PASS. `dotnet build Naudit.slnx` grün.

- [ ] **Step 3: Commit** — `feat(gitlab): Per-Projekt-Token pro Request (PRIVATE-TOKEN aus IGitTokenProvider)`.

---

### Task 4: Doku + Config-Defaults

**Files:**
- Modify: `docs/configuration.md` — zwei Key-Zeilen + kurzer Abschnitt „Per-project tokens".
- Modify: `docs/platform-setup.md` — Hinweis, dass fine-grained Tokens pro Projekt hinterlegt werden können (Fallback = globaler Token).
- Modify: `src/Naudit.Web/appsettings.json` — additiv `"ProjectTokens": {}` unter `GitHub`/`GitLab` (Struktur-Hinweis, **keine** echten Tokens; die kommen aus user-secrets).
- Modify: `CLAUDE.md` — Request-flow/Extension-Point-Notiz: Token wird pro Projekt über `IGitTokenProvider` aufgelöst (Default config-basiert, Naht für späteren Secret-Store).

`docs/configuration.md` — neue Zeilen in der Key-Tabelle:

```
| `Naudit:GitHub:ProjectTokens:<owner/repo>` | Optionaler fine-grained PAT nur für dieses Repo; Fallback = `Naudit:GitHub:Token` |
| `Naudit:GitLab:ProjectTokens:<projectId>`  | Optionaler Token nur für dieses Projekt (numerische ID); Fallback = `Naudit:GitLab:Token` |
```

Neuer Abschnitt (EN) mit user-secrets-Beispiel + Resolution/Fallback-Semantik + Key-Format-Hinweis (GitHub `owner/repo` case-insensitiv, GitLab **numerische** ID):

```bash
dotnet user-secrets set "Naudit:GitHub:ProjectTokens:octo/hello-world" "github_pat_..." --project src/Naudit.Web
```

- [ ] **Step 1:** configuration.md + platform-setup.md + appsettings.json + CLAUDE.md schreiben.
- [ ] **Step 2:** `dotnet build Naudit.slnx` grün (JSON valide).
- [ ] **Step 3: Commit** — `docs(tokens): Per-Projekt-Git-Tokens dokumentieren + appsettings-Struktur`.

---

### Task 5: Final — volle Suite + Self-Review

- [ ] **Step 1:** `dotnet test Naudit.slnx` — alle grün **außer** dem bekannten, fremden `SastWiringTests`-Fehler (s. o.). Keine **neuen** Fehler.
- [ ] **Step 2:** Self-Review über das Diff:
  - Core unangetastet (kein `Naudit.Core`-Diff)?
  - Kein Default-Auth-Header mehr in den typed Clients; **jeder** ausgehende Request (auch die N POSTs in GitLab `PostReviewAsync`) trägt den projekt-aufgelösten Token?
  - Ohne `ProjectTokens` identisches Verhalten (Default-Token)? Leerer Override ⇒ Default?
  - Kein Token im Log/Output; Clone-URL-Token aus dem Provider.
- [ ] **Step 3:** Memory/Board aktualisieren, PR öffnen.

## Verweise

- Token-Fluss heute: `src/Naudit.Infrastructure/DependencyInjection.cs` (eingebrannter Default-Header), `GitHubPlatform.GetCheckoutAsync` / `GitLabPlatform.GetCheckoutAsync` (Token direkt aus Options).
- ProjectId-Herkunft: `GitHubWebhook.ToReviewRequest` (`owner/repo`), `GitLabWebhook.ToReviewRequest` (numerische ID).
- Seam-Vorbild: `IPromptRedactor` / `IFindingReducer` (Interface-in-Naht + config-gewählte Impl).
- Test-Vorbild Wiring: `tests/Naudit.Tests/SastWiringTests.cs` (`Build`-Helper); HTTP-Assertion: `tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs` (`Calls`-Verlauf).
- Architektur & Core-Regel: `CLAUDE.md`.
