# GitHub-App-Auth (Bot-Identität) + echtes Review-Verdikt — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **Ausführungs-Setup (vom Nutzer gesetzt):** Orchestrierung mit **Opus 4.8**, Coding-Subagents mit **Sonnet 5**. Jeder Task ist bewusst in sich geschlossen (alle nötigen Snippets + Dateipfade stehen im Task) — ein Subagent braucht außer diesem Plan, `CLAUDE.md` und den genannten Dateien keinen weiteren Kontext.

**Goal:** Naudit kann als **GitHub App** (`Naudit[bot]`) auftreten — install-on-repo = 1 Klick, ein zentraler Webhook, kurzlebige Installation-Tokens (1 h) statt User-PAT — und ein **echtes Review-Verdikt** abgeben (`APPROVE`/`REQUEST_CHANGES` auf GitHub, `approve`/`unapprove` auf GitLab), abgeleitet aus dem bestehenden severity-bewussten Gate. Beides **opt-in**; ohne Umstellung verhält sich Naudit exakt wie heute.

**Architecture:** Zwei orthogonale Schalter, beide config-only:
1. `Naudit:GitHub:Auth = Pat | App` (Default `Pat`) wählt die `IGitTokenProvider`-Implementierung: `ConfiguredGitTokenProvider` (heute) oder neu `GitHubAppTokenProvider` (JWT RS256 via BCL → Installation-Lookup → Token-Mint, gecached bis ~5 min vor Ablauf). Gleiche Naht wie bei den Per-Projekt-Tokens — **kein Core-Eingriff**. Dafür wird die Naht **async** (`ResolveTokenAsync`), weil Token-Minting I/O ist (nur Infrastructure + Fakes).
2. `Naudit:GitHub:PostVerdict` / `Naudit:GitLab:PostVerdict` (bool, Default `false`) schaltet das echte Verdikt: `IGitPlatform.PostReviewAsync` bekommt den — schon Core-eigenen — `ReviewVerdict` als Parameter; GitHub mappt auf `event`, GitLab ruft `approve`/`unapprove`. Ohne Opt-in bleibt GitHub bei `event="COMMENT"` und GitLab postet keinen Approval-Call. **Bewusst orthogonal zu `Auth`** (funktioniert auch mit Service-Account-PAT/GitLab-Bot-Token); Hintergrund: GitHub lehnt `APPROVE`/`REQUEST_CHANGES` vom **PR-Autor** mit 422 ab — deshalb kein Auto-An und die App als empfohlener Weg.

**Tech Stack:** .NET 10, xUnit. Reine BCL: `System.Security.Cryptography.RSA` (`ImportFromPem`, `ExportRSAPrivateKeyPem` im Test), `System.Buffers.Text.Base64Url`, `TimeProvider`. **Kein neues NuGet-Paket, kein Dockerfile-Eingriff.**

**Branch:** `feat/github-app-auth` (von `main`).

## Global Constraints

- **Core-Regel:** `Naudit.Core` wird nur an **einer** Stelle angefasst: `IGitPlatform.PostReviewAsync` bekommt den Core-Typ `ReviewVerdict` als Parameter (+ `ReviewService`-Call-Site). Kein Provider-/Plattform-Wissen in Core; `IGitTokenProvider` bleibt komplett in Infrastructure.
- **Solution-Datei ist `Naudit.slnx`** (nicht `.sln`). Build: `dotnet build Naudit.slnx`. Tests: `dotnet test Naudit.slnx`.
- **TDD:** red → green, **ein Commit pro Task**.
- **Code-Kommentare auf Deutsch**; `README`/`docs/` auf Englisch.
- **Rückwärtskompatibel:** Default `Auth=Pat` + `PostVerdict=false` ⇒ byte-identisches Verhalten zu heute (GitHub-Payload weiterhin `event="COMMENT"`, GitLab ohne Approve-Call).
- **Secrets:** `AppId`/`PrivateKey`/`InstallationId` kommen aus user-secrets/Env/Coolify, **nie** in `appsettings.json`. Der **Private Key und Installation-Tokens dürfen nie geloggt werden** (Test verankert das). Clone-URLs enthalten Tokens — nicht loggen (bestehende Regel).
- **Arbeitsstand beachten:** `src/Naudit.Web/Properties/launchSettings.json` ist lokal modifiziert und gehört **nicht** in die Commits dieses Plans.

## Pre-existing test note

`SastWiringTests.Disabled_registersNoAnalyzers_butReviewServiceResolves` ist auf dieser Windows-Maschine **vor** dieser Arbeit rot (Windows-spezifisch; CI/Linux grün — siehe Memory). Nicht Teil dieses Plans; **nicht anfassen**. Kein Task hier ändert etwas an dieser Ursache; es dürfen nur keine **neuen** Fehler dazukommen.

## GitHub-App-API-Referenz (für Task 2)

- **App-JWT:** RS256; Claims `iat` (60 s rückdatiert gegen Clock-Skew), `exp` (max. 10 min; wir nehmen 9), `iss` = App-ID. Auth-Header `Authorization: Bearer <jwt>`.
- **Installation-Lookup:** `GET /repos/{owner}/{repo}/installation` (JWT-Auth) → `{ "id": 123, … }`. 404 ⇒ App ist in dem Repo nicht installiert (aussagekräftige Fehlermeldung werfen).
- **Token-Mint:** `POST /app/installations/{id}/access_tokens` (JWT-Auth) → `{ "token": "ghs_…", "expires_at": "2026-07-02T13:00:00Z", … }` (1 h gültig).
- Das Installation-Token funktioniert überall, wo heute der PAT steht: REST (`Authorization: Bearer`) **und** Clone-URL (`x-access-token:<token>@…`) — deshalb genügt die `IGitTokenProvider`-Naht, `GitHubPlatform` bleibt in Task 2 unberührt.

## File Structure

**Neu (Infrastructure):**
- `src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs` — App-JWT + Installation-Lookup + Token-Mint + Cache.

**Geändert (Infrastructure):**
- `src/Naudit.Infrastructure/Git/IGitTokenProvider.cs` — `ResolveToken` → `ResolveTokenAsync`.
- `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs` — `Auth` (enum `GitHubAuthKind`), `App` (`GitHubAppOptions`), `PostVerdict`.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabOptions.cs` — `PostVerdict`.
- `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` — `await` an der Naht; `event`-Mapping; Ctor + `IOptions<GitHubOptions>`.
- `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` — `await` an der Naht; approve/unapprove; Ctor + `IOptions<GitLabOptions>`.
- `src/Naudit.Infrastructure/DependencyInjection.cs` — Auth-Switch im GitHub-Zweig + named HttpClient fürs Minting.

**Geändert (Core — einzige Core-Änderung):**
- `src/Naudit.Core/Abstractions/IGitPlatform.cs` — `PostReviewAsync(…, ReviewVerdict verdict, …)`.
- `src/Naudit.Core/Review/ReviewService.cs` — Verdikt an `PostReviewAsync` durchreichen (eine Zeile).

**Neu (Tests):**
- `tests/Naudit.Tests/GitHubAppTokenProviderTests.cs`

**Geändert (Tests):**
- `tests/Naudit.Tests/GitTokenProviderTests.cs`, `GitTokenWiringTests.cs` — async-Naht + Auth-Switch-Wiring.
- `tests/Naudit.Tests/GitHubPlatformTests.cs`, `GitLabPlatformTests.cs` — neue Ctors/Signatur + Verdikt-Tests.
- `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs` — Verdikt-Parameter + `PostedVerdict`.
- `tests/Naudit.Tests/ReviewServiceTests.cs` — Verdikt-Durchreichung asserten.

**Geändert (Docs/Config):**
- Neu: `docs/github-app.md`. Geändert: `docs/configuration.md`, `docs/platform-setup.md`, `docs/deployment.md` (Env-Template), `CLAUDE.md`, `src/Naudit.Web/appsettings.json`.

---

### Task 1: Naht async — `ResolveToken` → `ResolveTokenAsync` (reines Refactoring, Suite bleibt grün)

Kein Verhaltenswechsel; nur die Signatur der Infrastructure-Naht wird async, damit Task 2 I/O machen darf. Kein RED-Schritt (Refactoring unter bestehenden Tests) — die bestehenden Tests werden mit umgestellt und bleiben die Absicherung.

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/IGitTokenProvider.cs`
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` (2 Call-Sites: `SendAsync` Z. 69, `GetCheckoutAsync` Z. 61)
- Modify: `src/Naudit.Infrastructure/Git/GitLab/GitLabPlatform.cs` (2 Call-Sites: `SendAsync` Z. 85, `GetCheckoutAsync` Z. 77)
- Modify: `tests/Naudit.Tests/GitTokenProviderTests.cs` (5 Tests), `tests/Naudit.Tests/GitTokenWiringTests.cs` (3 Tests)

- [ ] **Step 1: Interface + `ConfiguredGitTokenProvider` umstellen**

`IGitTokenProvider.cs` — Interface-Methode ersetzen (XML-Doc sinngemäß erhalten, Hinweis ergänzen warum async):

```csharp
/// <summary>Per-Projekt-Override, sonst der globale Default-Token. Async, weil Implementierungen
/// Tokens zur Laufzeit minten können (GitHub-App-Installation-Tokens = HTTP-I/O).</summary>
ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default);
```

`ConfiguredGitTokenProvider`:

```csharp
public ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
    => ValueTask.FromResult(_projectTokens.TryGetValue(projectId, out var t) ? t : _defaultToken);
```

- [ ] **Step 2: Call-Sites `await`en**

`GitHubPlatform.cs`:

```csharp
// SendAsync:
req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.ResolveTokenAsync(projectId, ct));

// GetCheckoutAsync (Token vorab auflösen, dann in die URL einbetten):
var token = await tokens.ResolveTokenAsync(request.ProjectId, ct);
var cloneUrl = repo.CloneUrl.Replace("://", $"://x-access-token:{token}@");
```

`GitLabPlatform.cs` analog (`PRIVATE-TOKEN`-Header in `SendAsync`; `oauth2:{token}@` in `GetCheckoutAsync`).

- [ ] **Step 3: Tests umstellen**

`GitTokenProviderTests`: alle 5 Tests `async Task` + `await p.ResolveTokenAsync(…)`. `GitTokenWiringTests`: alle 3 Tests analog. Keine neuen Tests, keine geänderten Assertions.

- [ ] **Step 4:** Run: `dotnet test Naudit.slnx` → Expected: PASS (bis auf den bekannten `SastWiringTests`-Fehler, s. o.).
- [ ] **Step 5: Commit** — `refactor(infra): IGitTokenProvider.ResolveToken -> ResolveTokenAsync (Naht async, kein Verhaltenswechsel)`

---

### Task 2: `GitHubAppTokenProvider` + `GitHubAppOptions` (TDD)

Der App-Auth-Kern: JWT bauen, Installation auflösen, Token minten, cachen. Wird in diesem Task **noch nicht** registriert (das macht Task 3) — die Suite bleibt jederzeit grün.

**Files:**
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs`
- Create: `src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs`
- Create: `tests/Naudit.Tests/GitHubAppTokenProviderTests.cs`

**Interfaces/Options (Ziel):**

```csharp
// In GitHubOptions.cs ergänzen:
public GitHubAuthKind Auth { get; set; } = GitHubAuthKind.Pat;  // Pat = heutiges Verhalten
public GitHubAppOptions App { get; set; } = new();

public enum GitHubAuthKind { Pat, App }

public sealed class GitHubAppOptions
{
    public string AppId { get; set; } = "";        // numerische App-ID (JWT-iss)
    public string PrivateKey { get; set; } = "";   // PEM — oder Base64-codiertes PEM (env-freundlich)
    public long? InstallationId { get; set; }      // optional: fest verdrahtet, spart den Lookup
}
```

- [ ] **Step 1 (RED): `GitHubAppTokenProviderTests` schreiben**

Test-Gerüst — RSA-Key wird **im Test generiert** (kein Fixture-Secret im Repo), Zeit läuft über einen trivialen `TimeProvider`-Fake, HTTP über den vorhandenen `StubHttpMessageHandler` (Routing nach Pfad):

```csharp
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Naudit.Infrastructure.Git.GitHub;
using Naudit.Tests.Fakes;
using Xunit;

namespace Naudit.Tests;

public class GitHubAppTokenProviderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<GitHubAppTokenProvider>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            => Messages.Add(fmt(state, ex));
    }

    // Stub-API: GET …/installation → {"id":123}; POST …/access_tokens → Token mit expires_at.
    private static StubHttpMessageHandler AppApi(Func<DateTimeOffset> expiresAt)
        => new(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/installation")
                ? """{"id":123}"""
                : $$"""{"token":"ghs_test","expires_at":"{{expiresAt():O}}"}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

    private static GitHubAppTokenProvider Provider(
        RSA rsa, StubHttpMessageHandler stub, TimeProvider time, ILogger<GitHubAppTokenProvider>? log = null,
        long? installationId = null, bool base64Pem = false)
    {
        var pem = rsa.ExportRSAPrivateKeyPem();
        var options = new GitHubAppOptions
        {
            AppId = "12345",
            PrivateKey = base64Pem ? Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)) : pem,
            InstallationId = installationId,
        };
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") };
        return new GitHubAppTokenProvider(http, options, log ?? new CapturingLogger(), time);
    }

    [Fact]
    public async Task ResolveTokenAsync_mintsInstallationToken_viaJwtAndLookup()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0));

        var token = await provider.ResolveTokenAsync("octo/hello-world");

        Assert.Equal("ghs_test", token);
        // Call 1 = Installation-Lookup, Call 2 = Token-Mint — beide mit JWT (Bearer, 3 Segmente).
        Assert.Equal(2, stub.Calls.Count);
        Assert.EndsWith("/repos/octo/hello-world/installation", stub.Calls[0].Uri!.AbsolutePath);
        Assert.EndsWith("/app/installations/123/access_tokens", stub.Calls[1].Uri!.AbsolutePath);
        var jwt = stub.Requests[0].Headers.Authorization!;
        Assert.Equal("Bearer", jwt.Scheme);
        var parts = jwt.Parameter!.Split('.');
        Assert.Equal(3, parts.Length);
        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(parts[1]));
        Assert.Contains("\"iss\":\"12345\"", payload);
        // Signatur mit dem Public Key verifizieren (echtes RS256, kein Fake-JWT).
        Assert.True(rsa.VerifyData(
            Encoding.UTF8.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public async Task ResolveTokenAsync_cachesToken_andRefreshesShortlyBeforeExpiry()
    {
        using var rsa = RSA.Create(2048);
        var time = new FakeTime(T0);
        var stub = AppApi(() => time.Now.AddHours(1));
        var provider = Provider(rsa, stub, time);

        await provider.ResolveTokenAsync("octo/hello-world");
        await provider.ResolveTokenAsync("octo/hello-world");
        // 2. Aufruf aus dem Cache: weiterhin nur 1 Lookup + 1 Mint.
        Assert.Equal(2, stub.Calls.Count);

        // Bis kurz vor Ablauf (Refresh-Fenster 5 min) bleibt der Cache gültig …
        time.Now = T0.AddMinutes(54);
        await provider.ResolveTokenAsync("octo/hello-world");
        Assert.Equal(2, stub.Calls.Count);

        // … danach wird neu gemintet (Installation-Id bleibt gecached: nur +1 Mint, kein 2. Lookup).
        time.Now = T0.AddMinutes(56);
        await provider.ResolveTokenAsync("octo/hello-world");
        Assert.Equal(3, stub.Calls.Count);
        Assert.EndsWith("/access_tokens", stub.Calls[2].Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ResolveTokenAsync_usesConfiguredInstallationId_withoutLookup()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), installationId: 777);

        await provider.ResolveTokenAsync("octo/hello-world");

        var call = Assert.Single(stub.Calls);
        Assert.EndsWith("/app/installations/777/access_tokens", call.Uri!.AbsolutePath);
    }

    [Fact]
    public async Task ResolveTokenAsync_acceptsBase64EncodedPem()
    {
        using var rsa = RSA.Create(2048);
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), base64Pem: true);

        Assert.Equal("ghs_test", await provider.ResolveTokenAsync("octo/hello-world"));
    }

    [Fact]
    public async Task ResolveTokenAsync_notInstalled_throwsWithProjectId()
    {
        using var rsa = RSA.Create(2048);
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var provider = Provider(rsa, stub, new FakeTime(T0));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ResolveTokenAsync("octo/hello-world").AsTask());
        Assert.Contains("octo/hello-world", ex.Message);
    }

    [Fact]
    public async Task Minting_neverLogsPrivateKeyOrToken()
    {
        using var rsa = RSA.Create(2048);
        var log = new CapturingLogger();
        var stub = AppApi(() => T0.AddHours(1));
        var provider = Provider(rsa, stub, new FakeTime(T0), log);

        await provider.ResolveTokenAsync("octo/hello-world");

        Assert.DoesNotContain(log.Messages, m => m.Contains("ghs_test") || m.Contains("PRIVATE KEY"));
    }
}
```

Run: `dotnet test Naudit.slnx --filter GitHubAppTokenProviderTests` → Expected: FAIL (Typ existiert nicht).

- [ ] **Step 2 (GREEN): Options + Provider implementieren**

`GitHubOptions.cs`: `Auth`/`App`/`GitHubAuthKind`/`GitHubAppOptions` wie oben ergänzen (deutsche Kommentare).

`src/Naudit.Infrastructure/Git/GitHub/GitHubAppTokenProvider.cs` — Gerüst (Subagent darf Details ausformulieren, Verhalten ist durch die Tests fixiert):

```csharp
using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Naudit.Infrastructure.Git.GitHub;

/// <summary>IGitTokenProvider für den GitHub-App-Modus: mintet kurzlebige Installation-Tokens
/// (App-JWT → Installation-Lookup → access_tokens) und cached sie bis kurz vor Ablauf.
/// Private Key und gemintete Tokens dürfen NIE geloggt werden.</summary>
public sealed class GitHubAppTokenProvider(
    HttpClient http, GitHubAppOptions options, ILogger<GitHubAppTokenProvider> logger, TimeProvider? time = null)
    : IGitTokenProvider
{
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(5);   // vor Ablauf erneuern
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);                          // ein Mint zur Zeit reicht (selten)
    private readonly Dictionary<string, long> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<long, (string Token, DateTimeOffset ExpiresAt)> _tokens = new();

    public async ValueTask<string> ResolveTokenAsync(string projectId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var installationId = options.InstallationId ?? await GetInstallationIdAsync(projectId, ct);
            if (_tokens.TryGetValue(installationId, out var cached) && _time.GetUtcNow() < cached.ExpiresAt - RefreshSkew)
                return cached.Token;

            var minted = await MintAsync(installationId, ct);
            _tokens[installationId] = minted;
            // Bewusst nur Metadaten loggen — nie Token oder Key.
            logger.LogInformation("GitHub-App-Installation-Token erneuert (Installation {InstallationId}, gültig bis {ExpiresAt:O}).",
                installationId, minted.ExpiresAt);
            return minted.Token;
        }
        finally { _gate.Release(); }
    }

    private async Task<long> GetInstallationIdAsync(string projectId, CancellationToken ct)
    {
        if (_installations.TryGetValue(projectId, out var known))
            return known;

        using var resp = await SendWithJwtAsync(HttpMethod.Get, $"repos/{projectId}/installation", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException(
                $"Die GitHub App (AppId {options.AppId}) ist im Repo '{projectId}' nicht installiert — erst \"Install app\" ausführen.");
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<InstallationDto>(ct)
            ?? throw new InvalidOperationException("GitHub lieferte keine Installation.");
        _installations[projectId] = dto.Id;
        return dto.Id;
    }

    private async Task<(string Token, DateTimeOffset ExpiresAt)> MintAsync(long installationId, CancellationToken ct)
    {
        using var resp = await SendWithJwtAsync(HttpMethod.Post, $"app/installations/{installationId}/access_tokens", ct);
        resp.EnsureSuccessStatusCode();
        var dto = await resp.Content.ReadFromJsonAsync<InstallationTokenDto>(ct);
        if (dto?.Token is not { Length: > 0 })
            throw new InvalidOperationException("GitHub lieferte kein Installation-Token.");
        return (dto.Token, dto.ExpiresAt);
    }

    private async Task<HttpResponseMessage> SendWithJwtAsync(HttpMethod method, string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CreateJwt());
        return await http.SendAsync(req, ct);
    }

    // App-JWT: RS256, iat 60 s rückdatiert (Clock-Skew), exp 9 min (< GitHub-Maximum 10 min).
    private string CreateJwt()
    {
        var now = _time.GetUtcNow();
        var header = """{"alg":"RS256","typ":"JWT"}""";
        var payload = $$"""{"iat":{{now.AddSeconds(-60).ToUnixTimeSeconds()}},"exp":{{now.AddMinutes(9).ToUnixTimeSeconds()}},"iss":"{{options.AppId}}"}""";
        var signingInput = $"{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header))}.{Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload))}";
        using var rsa = RSA.Create();
        rsa.ImportFromPem(LoadPem(options.PrivateKey));
        var signature = rsa.SignData(Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    // Env-freundlich: roher PEM-Text ODER Base64-codiertes PEM (Coolify/Docker-Env ohne Zeilenumbrüche).
    private static string LoadPem(string key)
        => key.Contains("-----BEGIN", StringComparison.Ordinal)
            ? key
            : Encoding.UTF8.GetString(Convert.FromBase64String(key));

    private sealed record InstallationDto([property: JsonPropertyName("id")] long Id);
    private sealed record InstallationTokenDto(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt);
}
```

Run: `dotnet test Naudit.slnx --filter GitHubAppTokenProviderTests` → Expected: PASS.

- [ ] **Step 3: Commit** — `feat(github): GitHubAppTokenProvider — App-JWT, Installation-Lookup, Token-Cache`

---

### Task 3: DI-Switch `Naudit:GitHub:Auth = Pat | App` (TDD)

**Files:**
- Modify: `tests/Naudit.Tests/GitTokenWiringTests.cs`
- Modify: `src/Naudit.Infrastructure/DependencyInjection.cs`

- [ ] **Step 1 (RED): Wiring-Tests ergänzen**

In `GitTokenWiringTests` (nutzt den vorhandenen `Build`-Helper; PEM einmal statisch generieren):

```csharp
private static readonly string TestPem = CreateTestPem();
private static string CreateTestPem()
{
    using var rsa = System.Security.Cryptography.RSA.Create(2048);
    return rsa.ExportRSAPrivateKeyPem();
}

[Fact]
public void GitHub_authApp_resolvesGitHubAppTokenProvider()
{
    using var sp = Build(new()
    {
        ["Naudit:Git:Platform"] = "GitHub",
        ["Naudit:GitHub:Auth"] = "App",
        ["Naudit:GitHub:App:AppId"] = "12345",
        ["Naudit:GitHub:App:PrivateKey"] = TestPem,
    });
    Assert.IsType<GitHubAppTokenProvider>(sp.GetRequiredService<IGitTokenProvider>());
}

[Fact]
public void GitHub_defaultAuth_resolvesConfiguredGitTokenProvider()
{
    using var sp = Build(new()
    {
        ["Naudit:Git:Platform"] = "GitHub",
        ["Naudit:GitHub:Token"] = "tok",
    });
    Assert.IsType<ConfiguredGitTokenProvider>(sp.GetRequiredService<IGitTokenProvider>());
}

[Fact]
public void GitHub_authApp_withoutKey_failsFastAtStartup()
{
    Assert.Throws<InvalidOperationException>(() => Build(new()
    {
        ["Naudit:Git:Platform"] = "GitHub",
        ["Naudit:GitHub:Auth"] = "App",
        ["Naudit:GitHub:App:AppId"] = "12345",
        // PrivateKey fehlt absichtlich
    }));
}
```

(`using Naudit.Infrastructure.Git.GitHub;` ergänzen.)

Run: `dotnet test Naudit.slnx --filter GitTokenWiringTests` → Expected: FAIL.

- [ ] **Step 2 (GREEN): GitHub-Zweig in `AddNauditInfrastructure` erweitern**

Die bestehende `AddSingleton<IGitTokenProvider>`-Zeile im GitHub-Zweig ersetzen durch:

```csharp
if (gitHubOptions.Auth == GitHubAuthKind.App)
{
    // Fail-fast beim Start statt kryptischem Fehler beim ersten Review.
    if (string.IsNullOrWhiteSpace(gitHubOptions.App.AppId) || string.IsNullOrWhiteSpace(gitHubOptions.App.PrivateKey))
        throw new InvalidOperationException("Naudit:GitHub:Auth=App verlangt Naudit:GitHub:App:AppId und Naudit:GitHub:App:PrivateKey.");

    // Eigener named Client fürs Token-Minting (JWT-Auth pro Request; gleiche Basis-Header wie die API).
    // Bewusst Singleton-Provider mit einmal erzeugtem Client: Minting ist selten (~1×/h), Ziel-Host fix.
    services.AddHttpClient("github-app", http =>
    {
        http.BaseAddress = new Uri(gitHubOptions.BaseUrl.TrimEnd('/') + "/");
        http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Naudit");
    });
    services.AddSingleton<IGitTokenProvider>(sp => new GitHubAppTokenProvider(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("github-app"),
        gitHubOptions.App,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<GitHubAppTokenProvider>()));
}
else
{
    services.AddSingleton<IGitTokenProvider>(new ConfiguredGitTokenProvider(gitHubOptions.Token, gitHubOptions.ProjectTokens));
}
```

Run: `dotnet test Naudit.slnx --filter GitTokenWiringTests` → Expected: PASS. Zusätzlich `dotnet build Naudit.slnx` grün.

- [ ] **Step 3: Commit** — `feat(infra): Auth-Switch Pat|App im DI (Naudit:GitHub:Auth, fail-fast bei fehlender App-Config)`

---

### Task 4: Echtes Verdikt — `PostReviewAsync(…, ReviewVerdict)` + `event`-Mapping + GitLab-Approve (TDD)

Die **einzige Core-Änderung** des Plans (Core-Typ über die Core-Naht ⇒ Regel intakt). Opt-in via `PostVerdict` (Default `false` ⇒ heutiges Verhalten).

**Files:**
- Modify: `src/Naudit.Core/Abstractions/IGitPlatform.cs`
- Modify: `src/Naudit.Core/Review/ReviewService.cs` (Z. 83: Verdikt durchreichen)
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubOptions.cs` + `…/GitLab/GitLabOptions.cs` (`PostVerdict`)
- Modify: `src/Naudit.Infrastructure/Git/GitHub/GitHubPlatform.cs` + `…/GitLab/GitLabPlatform.cs`
- Modify: `tests/Naudit.Tests/Fakes/FakeGitPlatform.cs`
- Modify: `tests/Naudit.Tests/GitHubPlatformTests.cs`, `GitLabPlatformTests.cs`, `ReviewServiceTests.cs`

- [ ] **Step 1 (RED): Tests schreiben/umstellen**

Signatur überall: `PostReviewAsync(request, summary, comments, verdict, ct)`. Bestands-Tests bekommen `ReviewVerdict.Approve` als 4. Argument; die Plattform-Ctors zusätzlich `Options.Create(new GitHubOptions())` bzw. `Options.Create(new GitLabOptions())` (`using Microsoft.Extensions.Options;`).

`GitHubPlatformTests` — neue Tests:

```csharp
[Fact]
public async Task PostReviewAsync_defaultOptions_keepsEventComment()
{
    var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "{}", capture), Tokens(),
        Options.Create(new GitHubOptions()));  // PostVerdict = false (Default)

    await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.RequestChanges);

    Assert.Contains("\"event\":\"COMMENT\"", capture.LastRequestBody);
}

[Theory]
[InlineData(ReviewVerdict.Approve, "APPROVE")]
[InlineData(ReviewVerdict.RequestChanges, "REQUEST_CHANGES")]
public async Task PostReviewAsync_postVerdict_mapsVerdictToEvent(ReviewVerdict verdict, string expectedEvent)
{
    var capture = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
    var platform = new GitHubPlatform(ClientReturning(HttpStatusCode.OK, "{}", capture), Tokens(),
        Options.Create(new GitHubOptions { PostVerdict = true }));

    await platform.PostReviewAsync(Request, "## Naudit Review", [], verdict);

    Assert.Contains($"\"event\":\"{expectedEvent}\"", capture.LastRequestBody);
}
```

`GitLabPlatformTests` — neue Tests (`Request.ProjectId == "1"`, `Iid == 42`; Responder liefert für alle Pfade 200 + `{}`):

```csharp
[Fact]
public async Task PostReviewAsync_postVerdictApprove_callsApproveEndpoint()
{
    var capture = new StubHttpMessageHandler(_ => Ok());
    var platform = new GitLabPlatform(ClientReturning(capture), Tokens(),
        Options.Create(new GitLabOptions { PostVerdict = true }));

    await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

    Assert.Contains(capture.Calls, c =>
        c.Method == HttpMethod.Post && c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/approve"));
}

[Fact]
public async Task PostReviewAsync_postVerdictRequestChanges_callsUnapprove_andTolerates404()
{
    // Unapprove antwortet 404, wenn es keine bestehende Approval gibt — das darf nicht werfen.
    var capture = new StubHttpMessageHandler(req =>
        req.RequestUri!.AbsolutePath.EndsWith("/unapprove")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : Ok());
    var platform = new GitLabPlatform(ClientReturning(capture), Tokens(),
        Options.Create(new GitLabOptions { PostVerdict = true }));

    await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.RequestChanges);

    Assert.Contains(capture.Calls, c =>
        c.Method == HttpMethod.Post && c.Uri!.AbsolutePath.EndsWith("/merge_requests/42/unapprove"));
}

[Fact]
public async Task PostReviewAsync_defaultOptions_postsNoApprovalCall()
{
    var capture = new StubHttpMessageHandler(_ => Ok());
    var platform = new GitLabPlatform(ClientReturning(capture), Tokens(),
        Options.Create(new GitLabOptions()));  // PostVerdict = false

    await platform.PostReviewAsync(Request, "## Naudit Review", [], ReviewVerdict.Approve);

    Assert.DoesNotContain(capture.Calls, c => c.Uri!.AbsolutePath.EndsWith("approve"));
}
```

(Helper `Ok()`/`ClientReturning(capture)` an die vorhandenen Test-Helper der Datei anlehnen — nicht neu erfinden.)

`FakeGitPlatform` — Signatur + Capture:

```csharp
public ReviewVerdict? PostedVerdict { get; private set; }

public Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default)
{
    PostedMarkdown = summaryMarkdown;
    PostedComments = comments;
    PostedVerdict = verdict;
    PostCallCount++;
    return Task.CompletedTask;
}
```

`ReviewServiceTests` — ein neuer Test: Review mit blockierendem Finding ⇒ `fakePlatform.PostedVerdict == ReviewVerdict.RequestChanges` (und im Approve-Fall analog `Approve`). An bestehende Testdaten der Datei anlehnen.

Run: `dotnet test Naudit.slnx --filter "GitHubPlatformTests|GitLabPlatformTests|ReviewServiceTests"` → Expected: FAIL (Signatur existiert nicht).

- [ ] **Step 2 (GREEN): Interface + Implementierungen**

`IGitPlatform.cs`:

```csharp
/// <summary>Postet den Summary-Kommentar und alle Inline-Kommentare an ihre Diff-Positionen.
/// Das Verdikt stammt aus dem severity-bewussten Gate; ob es als echter Review-Status gepostet
/// wird, entscheidet die Plattform-Konfiguration (PostVerdict, Default aus).</summary>
Task PostReviewAsync(ReviewRequest request, string summaryMarkdown, IReadOnlyList<InlineComment> comments, ReviewVerdict verdict, CancellationToken ct = default);
```

`ReviewService.cs` Z. 83: `await gitPlatform.PostReviewAsync(request, summary, inline, verdict, ct);`

`GitHubOptions`/`GitLabOptions`:

```csharp
// Opt-in: echtes Review-Verdikt posten (GitHub event=APPROVE/REQUEST_CHANGES bzw. GitLab approve).
// Default false = heutiges Verhalten. Achtung GitHub: vom PR-Autor lehnt die API das mit 422 ab
// — nur mit App-/Service-Account-Identität aktivieren.
public bool PostVerdict { get; set; }
```

`GitHubPlatform` — Ctor `((HttpClient http, IGitTokenProvider tokens, IOptions<GitHubOptions> options)`; im Payload:

```csharp
// Echtes Verdikt nur opt-in (PostVerdict) — Default bleibt COMMENT (kein Review-Status).
var @event = !options.Value.PostVerdict ? "COMMENT"
    : verdict == ReviewVerdict.RequestChanges ? "REQUEST_CHANGES" : "APPROVE";
```

(den bisherigen Kommentar „Naudit gatet nicht über GitHubs eigenen Review-Status" entsprechend anpassen — das gilt jetzt nur noch für den Default).

`GitLabPlatform` — Ctor analog `IOptions<GitLabOptions>`. **Achtung Kontrollfluss:** das bisherige `if (comments.Count == 0) return;` würde den Verdikt-Call verschlucken. Umbauen zu:

```csharp
if (comments.Count > 0)
{
    // … bestehender Block: diff_refs holen + je Kommentar eine Discussion posten …
}

// Echtes Verdikt (opt-in): Approve setzen bzw. eine frühere Approval zurückziehen.
if (options.Value.PostVerdict)
{
    if (verdict == ReviewVerdict.Approve)
    {
        (await SendAsync(HttpMethod.Post, $"{basePath}/approve", request.ProjectId, null, ct)).EnsureSuccessStatusCode();
    }
    else
    {
        // 404 = es gab keine Approval dieses Users — kein Fehler.
        using var resp = await SendAsync(HttpMethod.Post, $"{basePath}/unapprove", request.ProjectId, null, ct);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }
}
```

(`using System.Net;` ergänzen.)

Run: `dotnet test Naudit.slnx` → Expected: PASS (voller Lauf, weil Core-Signatur alle Schichten berührt; bekannter `SastWiringTests`-Fehler ausgenommen).

- [ ] **Step 3: Commit** — `feat(review): echtes Review-Verdikt — PostReviewAsync(verdict), GitHub-event-Mapping + GitLab-approve (opt-in PostVerdict)`

---

### Task 5: Doku + Config-Defaults

**Files:**
- Create: `docs/github-app.md`
- Modify: `docs/configuration.md`, `docs/platform-setup.md`, `docs/deployment.md`, `CLAUDE.md`, `src/Naudit.Web/appsettings.json`

- [ ] **Step 1: `docs/github-app.md` schreiben** (EN, Stil wie `docs/platform-setup.md`):
  - Why: bot identity `Naudit[bot]`, one-click install, central webhook, 1-h installation tokens, real (blocking) reviews — and why a PAT can't do that on the owner's own PRs (422).
  - **Create the app** (once): via GitHub UI (Settings → Developer settings → GitHub Apps → New GitHub App) **or** manifest flow; webhook URL `https://<host>/webhook/github`, webhook secret = `Naudit:GitHub:WebhookSecret`; permissions `pull_requests: write`, `contents: read`, `metadata: read`; subscribe to `pull_request`; generate + download the private key.
  - **Configure Naudit:** `Auth=App`, `App:AppId`, `App:PrivateKey` (raw PEM or base64 — recommend base64 for env vars), optional `App:InstallationId`, optional `PostVerdict=true`. user-secrets example + Coolify env example (`Naudit__GitHub__App__PrivateKey` = base64 PEM).
  - **Install:** "Install app" on repo/org — that's the whole per-repo integration.
  - GitLab analogue section: group access token (bot user) + one group webhook + `Naudit:GitLab:PostVerdict`; tier caveat.
- [ ] **Step 2: `docs/configuration.md`** — Key-Tabelle ergänzen:

```
| `Naudit:GitHub:Auth`               | `Pat` (default) or `App` — how Naudit authenticates against GitHub |
| `Naudit:GitHub:App:AppId`          | GitHub App ID (required for `Auth=App`) |
| `Naudit:GitHub:App:PrivateKey`     | App private key: raw PEM or base64-encoded PEM (required for `Auth=App`; secret!) |
| `Naudit:GitHub:App:InstallationId` | Optional: fixed installation id (skips the per-repo lookup) |
| `Naudit:GitHub:PostVerdict`        | `true` posts a real review state (`APPROVE`/`REQUEST_CHANGES`); default `false` = `COMMENT` |
| `Naudit:GitLab:PostVerdict`        | `true` calls MR `approve`/`unapprove` from the verdict; default `false` |
```

  Plus Hinweis: with `Auth=App`, `Token`/`ProjectTokens` are ignored; link to `docs/github-app.md`.
- [ ] **Step 3: `docs/platform-setup.md`** — GitHub-Abschnitt: App-Pfad als **recommended** voranstellen (Link auf `docs/github-app.md`), PAT-Pfad als dev/fallback degradieren. GitLab-Abschnitt: group bot + group webhook ergänzen.
- [ ] **Step 4: `docs/deployment.md`** — Coolify-Env-Template um die App-Variablen + `PostVerdict` erweitern.
- [ ] **Step 5: `CLAUDE.md`** — Extension-Points-Abschnitt: `GitHubAppTokenProvider` als zweite `IGitTokenProvider`-Impl (`Naudit:GitHub:Auth=Pat|App`), Naht jetzt async (`ResolveTokenAsync`); Request-flow-Absatz: `PostReviewAsync` trägt das abgeleitete Verdikt, echtes Posten opt-in via `PostVerdict`.
- [ ] **Step 6: `src/Naudit.Web/appsettings.json`** — Struktur-Hinweise (keine Secrets!): unter `GitHub` → `"Auth": "Pat"`, `"PostVerdict": false`, `"App": { "AppId": "", "PrivateKey": "", "InstallationId": null }`; unter `GitLab` → `"PostVerdict": false`.
- [ ] **Step 7:** `dotnet build Naudit.slnx` grün (JSON valide) + `dotnet test Naudit.slnx` unverändert.
- [ ] **Step 8: Commit** — `docs(github-app): Setup-Guide (App-Erstellung/Install/Coolify) + Konfiguration + CLAUDE.md`

---

### Task 6: Final — volle Suite + Self-Review + PR

- [ ] **Step 1:** `dotnet test Naudit.slnx` — alles grün außer dem bekannten, fremden `SastWiringTests`-Fehler (Windows-only). Keine **neuen** Fehler.
- [ ] **Step 2:** Self-Review über das volle Diff:
  - Core-Diff besteht **nur** aus dem `ReviewVerdict`-Parameter (`IGitPlatform` + `ReviewService`-Zeile)?
  - Default-Verhalten byte-identisch: `Auth=Pat` ⇒ `ConfiguredGitTokenProvider`; `PostVerdict=false` ⇒ GitHub `event="COMMENT"`, GitLab ohne approve/unapprove-Call?
  - Kein Private Key / kein Token in Logs, Exceptions oder Test-Fixtures (Keys werden in Tests generiert)?
  - GitLab: Verdikt-Call läuft auch bei `comments.Count == 0` (Early-Return entfernt)? Unapprove toleriert 404?
  - `GitHubAppTokenProvider`: Cache-Fenster (Refresh ~5 min vor Ablauf), Installation-Cache, SemaphoreSlim, JWT `exp` ≤ 10 min?
  - `launchSettings.json` **nicht** in den Commits?
  - `appsettings.json` enthält nur leere Struktur-Hinweise?
- [ ] **Step 3:** PR öffnen (`feat/github-app-auth` → `main`), Beschreibung mit Verweis auf Spec + Plan; Memory/Board aktualisieren (Doing-Item: „Umsetzung im PR").

## Verweise

- Spec: `docs/superpowers/specs/2026-07-02-github-app-auth-design.md`
- Vault-Design-Notiz: `BenediktsMind/1. Projects/Naudit/2026-07-02 Bot-Identität via GitHub App – Design.md`
- Naht + Vorarbeit: `docs/superpowers/plans/2026-07-01-per-project-git-tokens.md` (`IGitTokenProvider`, Auth pro Request)
- `event=COMMENT`-Vorentscheidung (hier bewusst revidiert): `docs/superpowers/specs/2026-06-22-inline-comments-design.md`
- Test-Vorbilder: `tests/Naudit.Tests/Fakes/StubHttpMessageHandler.cs` (`Calls`/`Requests`), `GitTokenWiringTests.Build`
- Architektur & Core-Regel: `CLAUDE.md`
